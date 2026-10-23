using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.CabControls;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Chat;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.Essentials;

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
            while (!ServerLedger.Loaded && waited < LedgerWait && player.PlayerId != 0)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (player.PlayerId == 0)
                yield break;
            if (!ServerLedger.Loaded)
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
                                $"(ledger: {string.Join(", ", ServerLedger.Ledger.LiveOwnedBy(OwnerRef.Player(owner)).Where(e => e.Prefab == prefab).Select(e => e.Id.ToString("D") + " " + e.Home))})");
                if (!has)
                    wanted.Add((prefab, null, true, ItemMoveReason.Issued, ClaimSlot(taken)));
            }
            bool kit = !ServerLedger.Ledger.HasStarterKit(owner);
            if (kit)
                foreach ((string prefab, string state) in StarterKit)
                    wanted.Add((prefab, state, false, ItemMoveReason.StarterKit, FreeSlot(taken, FirstKitSlot)));
            if (wanted.Count == 0)
                yield break;

            // The server knows every slot (OI-SLOT-01): with none free, what's made goes to their
            // Lost and Found instead (InventoryOverflow, OI-START-09), never to no slot.
            int made = 0;
            foreach (var item in wanted)
            {
                if (player.PlayerId == 0)
                    yield break;
                bool overflow = item.Slot < 0;
                bool ok = false;
                yield return Make(player, item.Prefab, item.State, item.Essential, item.Reason, item.Slot, overflow,
                    result => ok = result);
                if (ok) made++;
            }
            // Marked even if one of them failed: a kit is given once, as before.
            if (kit)
                ServerLedger.Ledger.MarkStarterKit(owner);
            Multiplayer.Log($"Starting items: made {made} of {wanted.Count} for {player.Username}" +
                            (kit ? " (starter kit included)" : ""));
        }
        finally
        {
            issuing.Remove(owner);
        }
    }

    /// <summary>Whether the player owns a live item of this prefab anywhere: the ledger alone (OI-START-04).</summary>
    private static bool Has(ServerPlayer player, string prefab) =>
        ServerLedger.Ledger.CountLive(OwnerRef.Player(player.Guid), prefab) > 0;

    // The slots that hold an item, owned or not, and the ones their essentials keep wherever those
    // are (a silhouette, or an essential in Lost and Found): what the ledger would refuse.
    private static HashSet<int> TakenSlots(ServerPlayer player)
    {
        OwnerRef owner = OwnerRef.Player(player.Guid);
        HashSet<int> taken = ServerLedger.Ledger.ClaimedSlots(owner);
        for (int slot = 0; slot < Slots; slot++)
            if (ServerLedger.Ledger.CanTake(Home.InSlot(player.Guid, slot), Guid.Empty, owner).Length > 0)
                taken.Add(slot);
        return taken;
    }

    // An essential's slot, as the ledger chooses a claim (OI-ESS-02): the lowest free one off the belt.
    private static int ClaimSlot(HashSet<int> taken)
    {
        int slot = InventorySlots.ChooseClaim(-1, taken, null);
        if (slot >= 0)
            taken.Add(slot);
        return slot;
    }

    // From the kit's first slot up, then from slot 0, never onto the belt (the VR belt, 33-35).
    private static int FreeSlot(HashSet<int> taken, int from)
    {
        for (int i = 0; i < InventorySlots.FirstBeltSlot; i++)
        {
            int slot = (from + i) % InventorySlots.FirstBeltSlot;
            if (taken.Add(slot))
                return slot;
        }
        return -1;
    }

    // As a cold-container withdrawal makes an item into a player's inventory
    // (ContainerServer.Withdraw): made at DV's safety position, its state loaded a
    // frame later, registered with its owner and slot, and sent to everyone. With overflow (their
    // inventory is full) it is made at their feet and goes straight to their Lost and Found, keeping
    // its claim: never into a full inventory, where it was in no slot at all (item-bug-review.md, E2a).
    private static IEnumerator Make(ServerPlayer player, string prefab, string state, bool essential,
        ItemMoveReason reason, int slot, bool overflow, Action<bool> done)
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

            if (overflow)
                instance.transform.position = player.AbsoluteWorldPosition + WorldMover.currentMove + Vector3.up * 0.3f;
            ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
            snapshot.ItemState = overflow ? ItemState.Dropped : ItemState.InInventory;
            snapshot.PlayerId = overflow ? (byte)0 : player.PlayerId;
            if (overflow)
                snapshot.ItemPosition = player.AbsoluteWorldPosition + Vector3.up * 0.3f;
            snapshot.InventorySlot = overflow ? -1 : slot;
            // The claim it asks for; the ledger gives it (OI-ESS-01).
            snapshot.InventoryClaimSlot = essential ? slot : -1;
            // An essential's slot is reserved for it (its silhouette, and recall).
            snapshot.InventoryClaimFlags = essential ? ItemInventoryClaimFlags.Reserved : ItemInventoryClaimFlags.None;
            ItemIdentity identity = instance.GetComponent<ItemIdentity>() ??
                                                   instance.AddComponent<ItemIdentity>();
            ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(reason, player,
                overflow ? "inventory full, slot " + slot + " kept" : "slot " + slot);
            if (LiveItems.RegisterMaterializedColdItem(item, player, player.Guid,
                    snapshot, identity.Ensure(), cause) == null)
                throw new InvalidOperationException("registration failed");
            item.ApplyServerCanonicalSnapshot(snapshot);
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
            if (!overflow && player.PlayerId == InventoryIntegration.LocalPlayerId && !InventoryIntegration.ContainsActive(instance))
                throw new InvalidOperationException("not in the host's own inventory");
            item.CompleteColdMaterialization();
            ContainerServer.PublishMaterializedItem(snapshot, item);
            // If Lost and Found refuses it, it stays at their feet: in sight either way.
            if (overflow && !LostAndFoundServer.Collect(item, LostItemReason.InventoryOverflow,
                    "made with the inventory full"))
                Multiplayer.LogWarning($"Starting items: {player.Username}'s inventory is full and Lost and Found refused their {prefab}; it is at their feet");
            else if (overflow)
                Multiplayer.Log($"Starting items: {player.Username}'s inventory is full; their {prefab} is in their Lost and Found");
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
