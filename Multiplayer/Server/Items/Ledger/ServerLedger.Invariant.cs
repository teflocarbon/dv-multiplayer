using DV.CabControls;
using DV.InventorySystem;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.Ledger;

/// <summary>
/// The one-home check at runtime (docs/design/items/ledger.md, "The invariant check"; OI-LED-15):
/// at load, before every save and, in Debug, every few seconds. What the server observes is
/// gathered here; the rules are <see cref="HomeInvariant"/>'s. A Debug build reports every breach
/// as an error (scenarios fail on it); a Release build logs it and repairs what it can (D7).
/// </summary>
public static partial class ServerLedger
{
    private const float DebugCheckSeconds = 5f;
    private static float nextCheck;
    private static readonly HashSet<string> reported = new();
    private static IReadOnlyList<InvariantViolation> lastViolations = Array.Empty<InvariantViolation>();

    /// <summary>What the last check found, for scenarios and the debug console.</summary>
    public static IReadOnlyList<InvariantViolation> LastViolations => lastViolations;

    private static void ClearInvariant()
    {
        nextCheck = 0;
        reported.Clear();
        lastViolations = Array.Empty<InvariantViolation>();
    }

    private static void InvariantTick()
    {
#if DEBUG
        if (!loaded || Time.realtimeSinceStartup < nextCheck)
            return;
        nextCheck = Time.realtimeSinceStartup + DebugCheckSeconds;
        CheckInvariant("debug-tick");
#endif
    }

    /// <summary>Runs the check now and reports what breaks it; each breach is logged once.</summary>
    public static IReadOnlyList<InvariantViolation> CheckInvariant(string when)
    {
        if (!loaded)
            return Array.Empty<InvariantViolation>();
        lastViolations = HomeInvariant.Check(ledger.All(), Observe(), new InvariantContext
        {
            DedicatedHost = NetworkLifecycle.Instance?.IsDedicatedHost == true,
            HostPlayer = NetworkServer.Instance?.LocalPlayer?.Guid ?? Guid.Empty
        });
        foreach (InvariantViolation violation in lastViolations)
        {
            if (reported.Add(violation.ToString()))
            {
                Multiplayer.LogError($"item.invariant-broken ({when}): {violation}");
                if (DebugRuntime.EnabledFor("inventory"))
                    DebugRuntime.Publish("inventory", "item.invariant-broken", DebugRuntimeSide.Server, DebugSeverity.Error,
                        "Item", violation.Id.ToString("D"), new Dictionary<string, object>
                        {
                            ["rule"] = (int)violation.Rule,
                            ["ruleName"] = violation.Rule.ToString(),
                            ["when"] = when,
                            ["detail"] = violation.Detail
                        });
            }
#if !DEBUG
            Repair(violation);
#endif
        }
        return lastViolations;
    }

    /// <summary>The check's lines, for <c>Library/Invariant.cs</c> and the debug console. Empty when it holds.</summary>
    public static List<string> CheckNow() => CheckInvariant("asked").Select(violation => violation.ToString()).ToList();

    // Release: the ledger wins on where (D7). A loose object somewhere else goes to its owner's Lost
    // and Found; one with a player is followed, since moving it would take it from their hands. What
    // can't be repaired without an object (lost, copied) stays logged for the load's recovery (R4).
    private static void Repair(InvariantViolation violation)
    {
        LiveItems.LiveItem record = violation.Id == Guid.Empty ? null : RecordOf(violation.Id);
        switch (violation.Rule)
        {
            case InvariantRule.LiveAgrees when record != null:
                if (HomeOf(record).IsLoose && record.Owner != Guid.Empty)
                    SendToLostAndFound(violation.Id, LostItemReason.SaveRecovery, "invariant repair", out _);
                else
                    ledger.Repair(violation.Id, HomeOf(record));
                break;
            case InvariantRule.NoStray when record != null && record.StoredOwner != Guid.Empty:
                Own(record, record.StoredOwner, BirthReason.Migrated, Guid.Empty, "invariant repair");
                break;
        }
    }

    private static LiveItems.LiveItem RecordOf(Guid id) =>
        netIdByPersistentId.TryGetValue(id, out ushort netId) && LiveItems.TryGet(netId, out LiveItems.LiveItem record) &&
        record.PersistentItemId == id ? record : LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == id);

    // Every copy of an item the server can see: its objects, a cold inventory's rows, Lost and
    // Found's pending entries and the containers' rows, with vanilla's lists for each object.
    // Inactive objects count (OI-LED-10): the records are walked, never FindObjectsOfType.
    private static IEnumerable<ObservedItem> Observe()
    {
        StorageController storage = StorageController.Instance;
        HashSet<ItemBase> inventory = Members(storage?.StorageInventory);
        HashSet<ItemBase> world = Members(storage?.StorageWorld);
        HashSet<ItemBase> lost = Members(storage?.StorageLostAndFound);
        HashSet<ItemBase> installed = Members(storage?.StorageInstalledGadgets);
        HashSet<Guid> pendingLost = PendingLostAndFound();
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
        {
            if (record.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.Container ||
                pendingLost.Contains(record.PersistentItemId) || record.PersistentItemId == Guid.Empty &&
                record.Placement != ItemPlacementKind.PlayerInventory)
                continue;
            ItemBase item = NetworkedItem.TryGet(record.NetId, out NetworkedItem networked) && networked != null
                ? networked.Item : null;
            VanillaLists lists = VanillaLists.None;
            if (item != null)
            {
                if (inventory.Contains(item)) lists |= VanillaLists.Inventory;
                if (world.Contains(item)) lists |= VanillaLists.World;
                if (lost.Contains(item)) lists |= VanillaLists.LostAndFound;
                if (installed.Contains(item)) lists |= VanillaLists.Installed;
            }
            yield return new ObservedItem
            {
                Id = record.PersistentItemId,
                Name = $"{record.PrefabName} netId {record.NetId}",
                Source = ObservedSource.LiveObject,
                Home = HomeOf(record),
                StoreOwner = record.StoredOwner == Guid.Empty ? OwnerRef.None : OwnerRef.Player(record.StoredOwner),
                Lists = lists
            };
        }
        foreach (ObservedItem cold in ColdRows())
            yield return cold;
        foreach (LostAndFoundServer.PendingEntry pending in LostAndFoundServer.PendingEntries())
            yield return new ObservedItem
            {
                Id = pending.ItemId,
                Name = pending.Prefab,
                Source = ObservedSource.LostAndFoundRow,
                Home = Home.LostAndFound,
                StoreOwner = OwnerRef.Player(pending.Owner)
            };
        foreach (StoredItemRecord row in ContainerServer.Graph.ExportSnapshot().Items)
        {
            Guid.TryParse(row.PersistentOwnerIdentity, out Guid owner);
            yield return new ObservedItem
            {
                Id = ItemIdOf(row),
                Name = row.PrefabName,
                Source = ObservedSource.ContainerRow,
                Home = Home.Container(row.ParentContainerId, row.Slot),
                StoreOwner = owner == Guid.Empty ? OwnerRef.None : OwnerRef.Player(owner)
            };
        }
    }

    /// <summary>Rows of cold inventories (D12), as the check sees them. Set by <c>Inventory/PlayerItems</c>.</summary>
    internal static Func<IEnumerable<ObservedItem>> ColdRows = Enumerable.Empty<ObservedItem>;

    private static HashSet<ItemBase> Members(StorageBase storage)
    {
        HashSet<ItemBase> members = new();
        if (storage == null)
            return members;
        foreach (ItemBase item in storage.GetStorageItemList())
            if (item != null)
                members.Add(item);
        return members;
    }
}
