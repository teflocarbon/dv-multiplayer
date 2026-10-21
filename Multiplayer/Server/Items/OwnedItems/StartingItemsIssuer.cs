using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.CabControls;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
using Multiplayer.Server.Chat;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.OwnedItems;

/// <summary>
/// The server makes each player's starting items, owned by them and into their inventory
/// (owned-items.md, "Joining and rejoining", PR 4). No game makes its own any more: DV's safeguards
/// are off in every game (Shared/Career/StartingItemsControllerPatch).
/// - The starter kit, once per player per career: the ledger's saved starter-kit marker.
/// - Each of DV's essentials for this career (comms radio, maps, compass, wallet, catalogue, ...)
///   the player has no live item of: in Lost and Found, in a container, with another player all
///   count. A destroyed one doesn't, so it is made again.
/// Licence papers aren't made in multiplayer (PhysicalLicensePaperPatch), so none are issued.
/// </summary>
public static class StartingItemsIssuer
{
    // What a new player used to get from the join packet, made by their own game.
    private static readonly (string Prefab, string State)[] StarterKit =
    [
        ("shovel", null), ("lighter", null), ("Oiler", null), ("Lantern", null),
        ("Flashlight", "{\"Restock\": true,\"Battery_power\": 100}"), ("Hanger", null), ("DuctTape", null)
    ];

    private const int Slots = 36;
    private const int FirstKitSlot = 14;
    private const float LedgerWait = 60f;

    private static readonly HashSet<Guid> issuing = new();

    /// <summary>Once the player has loaded (a client, or the host's own player): makes what they're missing.</summary>
    public static void OnPlayerLoaded(ServerPlayer player)
    {
        if (player == null || player.Guid == Guid.Empty)
            return;
        if (!issuing.Add(player.Guid))
        {
            Multiplayer.LogWarning($"Starting items: still making {player.Username}'s from before; not again");
            return;
        }
        CoroutineManager.Instance.StartCoroutine(Issue(player));
    }

    private static IEnumerator Issue(ServerPlayer player)
    {
        Guid owner = player.Guid;
        try
        {
            // The ledger decides what they have, so it waits for the stores' import and the check.
            float waited = 0f;
            while (!OwnedItemsServer.Loaded && waited < LedgerWait && player.PlayerId != 0)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (player.PlayerId == 0)
                yield break;
            if (!OwnedItemsServer.Loaded)
            {
                // Better nothing than a second set: their next join tries again.
                Multiplayer.LogWarning($"Starting items: the owned-items ledger didn't load in {LedgerWait:F0} s; nothing made for {player.Username}");
                yield break;
            }

            HashSet<int> taken = TakenSlots(player);
            List<(string Prefab, string State, bool Essential, ItemMoveReason Reason, int Slot)> wanted = new();
            foreach (string prefab in AdminItemGrants.StartingItemPrefabs().Distinct(StringComparer.Ordinal))
            {
                bool has = Has(player, prefab);
                Multiplayer.Log($"Starting items: {player.Username} {(has ? "has" : "lacks")} a {prefab} " +
                                $"(ledger: {string.Join(", ", OwnedItemsServer.Ledger.LiveOwnedBy(owner).Where(e => e.Prefab == prefab).Select(e => e.Id.ToString("D") + " " + e.Where.Place))})");
                if (!has)
                    wanted.Add((prefab, null, true, ItemMoveReason.Issued, FreeSlot(taken, 0)));
            }
            bool kit = !OwnedItemsServer.Ledger.HasStarterKit(owner);
            if (kit)
                foreach ((string prefab, string state) in StarterKit)
                    wanted.Add((prefab, state, false, ItemMoveReason.StarterKit, FreeSlot(taken, FirstKitSlot)));
            if (wanted.Count == 0)
                yield break;

            int made = 0;
            foreach (var item in wanted)
            {
                if (player.PlayerId == 0)
                    yield break;
                if (item.Slot < 0)
                {
                    Multiplayer.LogWarning($"Starting items: no free slot for {player.Username}'s {item.Prefab}");
                    continue;
                }
                bool ok = false;
                yield return Make(player, item.Prefab, item.State, item.Essential, item.Reason, item.Slot, result => ok = result);
                if (ok) made++;
            }
            // Marked even if one of them failed: a kit is given once, as before.
            if (kit)
                OwnedItemsServer.Ledger.MarkStarterKit(owner);
            Multiplayer.Log($"Starting items: made {made} of {wanted.Count} for {player.Username}" +
                            (kit ? " (starter kit included)" : ""));
        }
        finally
        {
            issuing.Remove(owner);
        }
    }

    /// <summary>Whether the player owns a live item of this prefab anywhere: the ledger, or the stores themselves.</summary>
    private static bool Has(ServerPlayer player, string prefab)
    {
        if (OwnedItemsServer.Ledger.CountLive(player.Guid, prefab) > 0)
            return true;
        if (!OwnedItemsSummary.For(player).Any(item => string.Equals(item.Prefab, prefab, StringComparison.Ordinal)))
            return false;
        // The ledger has none, but a store has one: nothing is made (no second copy), and where it
        // is gets logged, as the ledger is still in shadow (a destroyed radio wasn't made again at
        // the next rejoin, starting-items 2026-10-22).
        Multiplayer.LogWarning($"Starting items: the ledger has no live {prefab} of {player.Username}'s, but " +
                               StoresHolding(player, prefab) + "; none made");
        return true;
    }

    private static string StoresHolding(ServerPlayer player, string prefab)
    {
        List<string> found = new();
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
            if (string.Equals(record.PrefabName, prefab, StringComparison.Ordinal) &&
                (record.PersistentOwnerIdentity == player.Guid ||
                 record.PersistentOwnerIdentity == Guid.Empty && record.PersistentOwnerPlayerId == player.PlayerId))
                found.Add($"record {record.NetId} {record.PersistentItemId:D} {record.Placement}, last {record.LastMove.Reason}");
        int pending = NetworkedLostAndFoundManager.PendingPrefabsOwnedBy(player.Guid)
            .Count(p => string.Equals(p, prefab, StringComparison.Ordinal));
        if (pending > 0)
            found.Add($"{pending} saved Lost and Found entr{(pending == 1 ? "y" : "ies")} not restored");
        string identity = player.Guid.ToString("D");
        int stored = NetworkedColdContainerManager.Graph.ExportSnapshot().Items.Count(item =>
            string.Equals(item.PersistentOwnerIdentity, identity, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.PrefabName, prefab, StringComparison.Ordinal));
        if (stored > 0)
            found.Add($"{stored} in containers");
        return "the stores hold " + string.Join("; ", found);
    }

    // The slots the player's inventory items are in, and the ones their essentials keep wherever
    // those are (a silhouette, or an essential in Lost and Found).
    private static HashSet<int> TakenSlots(ServerPlayer player)
    {
        HashSet<int> taken = new();
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll())
        {
            if (record.InventoryClaimSlot < 0)
                continue;
            bool held = record.Placement == ItemPlacementKind.PlayerInventory &&
                        (record.PlacementPlayerIdentity == player.Guid || record.PlacementPlayerId == player.PlayerId);
            bool claimed = record.InventoryClaimPlayerId == player.PlayerId &&
                           (record.InventoryClaimFlags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;
            if (held || claimed)
                taken.Add(record.InventoryClaimSlot);
        }
        return taken;
    }

    private static int FreeSlot(HashSet<int> taken, int from)
    {
        for (int i = 0; i < Slots; i++)
        {
            int slot = (from + i) % Slots;
            if (taken.Add(slot))
                return slot;
        }
        return -1;
    }

    // As a cold-container withdrawal makes an item into a player's inventory
    // (NetworkedColdContainerManager.Withdraw): made at DV's safety position, its state loaded a
    // frame later, registered with its owner and slot, and sent to everyone.
    private static IEnumerator Make(ServerPlayer player, string prefab, string state, bool essential,
        ItemMoveReason reason, int slot, Action<bool> done)
    {
        InventoryItemSpec spec = Globals.G?.Items?.items?.FirstOrDefault(candidate => candidate != null &&
            string.Equals(candidate.ItemPrefabName, prefab, StringComparison.Ordinal));
        if (spec == null)
        {
            Multiplayer.LogWarning($"Starting items: no item called {prefab}");
            done(false);
            yield break;
        }
        GameObject instance = Object.Instantiate(spec.gameObject,
            StartingItemsController.ITEM_INSTANTIATION_SAFETY_POSITION, Quaternion.identity);
        instance.name = prefab;
        InventoryItemSpec placed = instance.GetComponent<InventoryItemSpec>();
        if (placed != null)
            placed.BelongsToPlayer = true;
        NetworkedItem item = instance.GetOrAddComponent<NetworkedItem>();
        item.BeginColdMaterialization();

        // DV's own StartingItemsController waits a frame before loading an item's state.
        yield return null;

        try
        {
            if (player.PlayerId == 0)
                throw new InvalidOperationException("the player left");
            ItemSaveData saveData = instance.GetComponent<ItemSaveData>();
            if (saveData != null && !string.IsNullOrEmpty(state))
            {
                saveData.LoadItemData(JObject.Parse(state));
                saveData.PostLoadItemData();
            }
            ItemBase itemBase = instance.GetComponent<ItemBase>();
            if (itemBase == null || item.NetId == 0)
                throw new InvalidOperationException("no net id");
            item.Initialize(itemBase, item.NetId, true);
            item.FinaliseTrackedValuesAutomatically();

            ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);
            snapshot.ItemState = ItemState.InInventory;
            snapshot.PlayerId = player.PlayerId;
            snapshot.InventoryClaimPlayerId = player.PlayerId;
            snapshot.InventoryClaimSlot = slot;
            // An essential's slot is reserved for it (its silhouette, and recall).
            snapshot.InventoryClaimFlags = essential ? ItemInventoryClaimFlags.Reserved : ItemInventoryClaimFlags.None;
            PersistentWorldItemIdentity identity = instance.GetComponent<PersistentWorldItemIdentity>() ??
                                                   instance.AddComponent<PersistentWorldItemIdentity>();
            ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(reason, player, "slot " + slot);
            if (AuthoritativeItemRegistry.RegisterMaterializedColdItem(item, player, player.PlayerId, player.Guid,
                    snapshot, identity.Ensure(), cause) == null)
                throw new InvalidOperationException("registration failed");
            item.ApplyServerCanonicalSnapshot(snapshot);
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
            if (player.PlayerId == InventoryIntegration.LocalPlayerId && !InventoryIntegration.ContainsActive(instance))
                throw new InvalidOperationException("not in the host's own inventory");
            item.CompleteColdMaterialization();
            NetworkedColdContainerManager.PublishMaterializedItem(snapshot, item);
            done(true);
        }
        catch (Exception exception)
        {
            item.AbortColdMaterialization();
            Object.Destroy(instance);
            Multiplayer.LogWarning($"Starting items: couldn't make {player.Username}'s {prefab}: {exception.Message}");
            done(false);
        }
    }
}
