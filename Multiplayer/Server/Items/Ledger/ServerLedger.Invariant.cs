using DV.CabControls;
using DV.InventorySystem;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
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

    private static LiveItems.LiveItem RecordOf(Guid id) =>
        netIdByPersistentId.TryGetValue(id, out ushort netId) && LiveItems.TryGet(netId, out LiveItems.LiveItem record) &&
        record.PersistentItemId == id ? record : LiveItems.GetAll().FirstOrDefault(r => r.PersistentItemId == id);

    // Every copy of an item the server can see: its objects, a cold inventory's rows and the
    // containers' rows, with vanilla's lists for each object.
    // Inactive objects count (OI-LED-10): the records are walked, never FindObjectsOfType.
    private static IEnumerable<ObservedItem> Observe()
    {
        StorageController storage = StorageController.Instance;
        HashSet<ItemBase> inventory = Members(storage?.StorageInventory);
        HashSet<ItemBase> world = Members(storage?.StorageWorld);
        HashSet<ItemBase> lost = Members(storage?.StorageLostAndFound);
        HashSet<ItemBase> installed = Members(storage?.StorageInstalledGadgets);
        foreach (LiveItems.LiveItem record in LiveItems.GetAll())
        {
            if (record.Placement is ItemPlacementKind.Destroyed or ItemPlacementKind.Container ||
                record.PersistentItemId == Guid.Empty &&
                record.Placement != ItemPlacementKind.PlayerInventory)
                continue;
            // Kept as data on the server: its row is the copy (ColdRows, LostRows).
            if (PlayerItems.IsDataOnly(record) || LostAndFoundServer.IsData(record.NetId))
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
        foreach (ObservedItem cold in ColdRows().Concat(AmmoRows()).Concat(LostRows()))
            yield return cold;
        foreach (StoredItemRecord row in ContainerServer.Graph.ExportSnapshot().Items)
        {
            yield return new ObservedItem
            {
                Id = row.PersistentItemId,
                Name = row.PrefabName,
                Source = ObservedSource.ContainerRow,
                Home = Home.Container(row.ParentContainerId, row.Slot),
                // A container row names no owner: its owner is the ledger's (containers.md).
                StoreOwner = OwnerRef.None
            };
        }
    }

    // Lost and Found's entries kept as data (OI-COLD-08).
    private static IEnumerable<ObservedItem> LostRows() => LostAndFoundServer.DataRows().Select(row => new ObservedItem
    {
        Id = row.PersistentItemId,
        Name = row.PrefabName,
        Source = ObservedSource.LostAndFoundRow,
        Home = Home.LostAndFound,
        StoreOwner = row.OwnerIdentity == Guid.Empty ? OwnerRef.None : OwnerRef.Player(row.OwnerIdentity)
    });

    // Cold inventories' rows (D12), and on the server the items in connected remote players'
    // inventories, kept as data (OI-COLD-06).
    private static IEnumerable<ObservedItem> ColdRows() => ColdInventories.All().Select(row => new ObservedItem
    {
        Id = row.PersistentItemId,
        Name = row.PrefabName,
        Source = ObservedSource.ColdRow,
        Home = Home.InSlot(row.PlacementPlayerIdentity, row.Slot),
        StoreOwner = row.OwnerIdentity == Guid.Empty ? OwnerRef.None : OwnerRef.Player(row.OwnerIdentity)
    });

    // Ammo in the magazine of a tool kept as data, kept as data with it (OI-CON-16).
    private static IEnumerable<ObservedItem> AmmoRows() => PlayerItems.AmmoRows().Select(row => new ObservedItem
    {
        Id = row.PersistentItemId,
        Name = row.PrefabName,
        Source = ObservedSource.ColdRow,
        Home = Home.Magazine(row.ContainerId, row.Slot),
        StoreOwner = row.OwnerIdentity == Guid.Empty ? OwnerRef.None : OwnerRef.Player(row.OwnerIdentity)
    });

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
