using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DV;
using DV.InventorySystem;
using DV.Items;
using DV.JObjectExtstensions;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.VanillaStorage;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.VanillaStorage;
using Multiplayer.Shared.Protocol.Data.Items;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.Containers;

/// <summary>
/// Ammo in a tool's magazine on the server (docs/design/items/containers.md, "Ammo in a magazine"):
/// an object loaded in its tool while the tool is an object, a row of the tool's container while the
/// tool is a row. The tool's object going (put in a box, gone cold with its leaving holder) takes its
/// ammo into its container as rows; the tool's object made again (taken out, the holder back) makes
/// them objects in its magazine; the tool destroyed for good lets them go, as a destroyed box does.
/// </summary>
internal static class MagazineServer
{
    /// <summary>
    /// A tool's object went (<see cref="ContainerServer.OnRemoved"/>). Called whatever the reason:
    /// its ammo goes with it into its container, or out of it if the tool is gone for good.
    /// </summary>
    internal static void OnToolRemoved(NetworkedItem tool, Guid toolId, ChangeCause<ItemRemovalReason> cause)
    {
        LiveItems.LiveItem[] ammo = LiveItems.AmmoIn(tool.NetId).ToArray();
        if (ammo.Length == 0 || cause.Reason == ItemRemovalReason.LeftInterest)
            return;
        // Unloading the world or quitting destroys every item without moving any of them.
        if (UnloadWatcher.isUnloading || UnloadWatcher.isQuitting || !tool.gameObject.scene.isLoaded)
            return;
        bool goesOn = cause.Reason is ItemRemovalReason.DepositedInContainer or ItemRemovalReason.WentCold or
            ItemRemovalReason.CollectedToLostAndFound or ItemRemovalReason.RemadeFresh or ItemRemovalReason.AuthoredSlotReplaced;
        Vector3 at = tool.transform.position - WorldMover.currentMove;
        foreach (LiveItems.LiveItem loaded in ammo)
        {
            if (!NetworkedItem.TryGet(loaded.NetId, out NetworkedItem item) || item == null)
                continue;
            string refused = "the tool has no persistent id";
            if (goesOn && toolId != Guid.Empty && Stow(tool, toolId, loaded, item, out refused))
                continue;
            if (goesOn)
                Multiplayer.LogWarning($"Magazine: {loaded.PrefabName} (netId {loaded.NetId}) couldn't go with its tool " +
                                       $"{tool.name} ({cause.Reason}): {refused}; it comes out instead");
            Release(item, loaded, at, cause);
        }
    }

    /// <summary>
    /// A live tool's container has rows (its ammo while it was a row): they are made objects in its
    /// magazine. Called by the server's shell scan once the tool is bound, so every way a tool comes
    /// back (taken out of an inventory, a box, Lost and Found; the load) is covered.
    /// </summary>
    internal static void LoadFromRows(NetworkedItem tool, Guid toolId)
    {
        ContainerGraph graph = ContainerServer.Graph;
        if (tool.GetComponentInChildren<ItemMagazine>(true) == null || !graph.IsContainer(toolId) ||
            !LiveItems.TryGet(tool.NetId, out LiveItems.LiveItem toolRecord) ||
            toolRecord.Placement is ItemPlacementKind.LostAndFound or ItemPlacementKind.Destroyed or ItemPlacementKind.Installed ||
            ItemSaveLoad.IsRestorePending(tool))
            return;
        foreach (StoredItemRecord stored in graph.RowsIn(toolId))
        {
            if (!graph.IsSettled(stored.PersistentItemId))
                continue;
            Guid.TryParse(graph.OwnerOf(stored.PersistentItemId), out Guid owner);
            Home stowed = Home.Container(toolId, stored.Slot), loaded = Home.Magazine(toolId, stored.Slot);
            ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(ItemMoveReason.LoadedIntoMagazine,
                detail: $"its tool {tool.name} is an object again");
            if (owner != Guid.Empty && ServerLedger.CommitStored(stored.PersistentItemId, stored.PrefabName, stowed, loaded, cause)
                    is { Length: > 0 } refused)
            {
                Multiplayer.LogWarning($"Magazine: {stored.PrefabName} ({stored.PersistentItemId:D}) stays a row of {tool.name}: the ledger refused, {refused}");
                continue;
            }
            SavedItemRow row = ItemSaveLoad.RowOfStored(stored);
            row.OwnerIdentity = owner;
            row.Placement = ItemPlacementKind.Magazine;
            row.ContainerId = toolId;
            row.Slot = stored.Slot;
            row.Position = tool.transform.position - WorldMover.currentMove;
            row.Rotation = Quaternion.identity;
            if (graph.TakeOut(stored.PersistentItemId, out string notTaken) == null)
            {
                Multiplayer.LogWarning($"Magazine: {stored.PrefabName} ({stored.PersistentItemId:D}) stays a row of {tool.name}: {notTaken}");
                MoveBack(stored, toolId, owner, loaded, notTaken);
                continue;
            }
            ItemSaveLoad.RecordAuthoredTaken(row);
            if (ItemSaveLoad.MakeLive(row, owner != Guid.Empty, out _))
                continue;
            // Not made: back in as a row, as it was.
            Multiplayer.LogWarning($"Magazine: {stored.PrefabName} ({stored.PersistentItemId:D}) couldn't be made in {tool.name}; it stays a row");
            if (!graph.Stow(stored, out string notStowed))
                Multiplayer.LogError($"Magazine: {stored.PrefabName} ({stored.PersistentItemId:D}) couldn't go back into {tool.name}: {notStowed}");
            MoveBack(stored, toolId, owner, loaded, "it couldn't be made");
        }
    }

    // Into the tool's container as a row: the ledger first, then the graph, then its object retires.
    private static bool Stow(NetworkedItem tool, Guid toolId, LiveItems.LiveItem loaded, NetworkedItem item, out string refused)
    {
        refused = string.Empty;
        ContainerServer.EnsureContainer(tool, toolId);
        if (!ContainerServer.Graph.IsContainer(toolId))
        {
            refused = "tool-has-no-container";
            return false;
        }
        if (item.IsSceneAuthored)
        {
            ItemSaveLoad.RecordDestroyed(item);
            LiveItems.PromoteAuthoredToPersistent(item);
            item.DetachAuthoredItemKey();
        }
        Guid id = loaded.PersistentItemId;
        if (id == Guid.Empty)
        {
            refused = "item-has-no-id";
            return false;
        }
        JObject state = item.GetComponent<ItemSaveData>()?.SaveItemData();
        StoredItemRecord row = new()
        {
            PersistentItemId = id,
            PrefabName = loaded.PrefabName,
            DisplayName = item.Item?.InventorySpecs?.LocalizedName ?? item.name,
            AuthoredItemKey = string.Empty,
            ParentContainerId = toolId,
            Slot = loaded.MagazineSlot,
            StateVersion = 1,
            DetachedState = state == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(state.ToString(Formatting.None))
        };
        bool owned = loaded.Owner != Guid.Empty;
        Home from = ServerLedger.HomeOf(loaded), to = Home.Container(toolId, row.Slot);
        ChangeCause<ItemMoveReason> moving = ItemChangeLog.Move(ItemMoveReason.DepositedInContainer,
            detail: $"with its tool {tool.name}, slot {row.Slot}");
        if (owned && (refused = ServerLedger.CommitStored(id, row.PrefabName, from, to, moving)).Length > 0)
            return false;
        if (!ContainerServer.Graph.Stow(row, out refused))
        {
            if (owned)
                ServerLedger.CommitStored(id, row.PrefabName, to, from,
                    ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "not stowed with its tool: " + refused));
            return false;
        }
        Retire(item, loaded, ItemChangeLog.Removal(ItemRemovalReason.DepositedInContainer,
            detail: $"with its tool {tool.name}, now cold record {id:D}"));
        return true;
    }

    // As a deposit retires an item: its Destroy goes now, by interest, then its object goes.
    private static void Retire(NetworkedItem item, LiveItems.LiveItem loaded, ChangeCause<ItemRemovalReason> cause)
    {
        ItemUpdate destroy = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Destroy);
        if (destroy != null)
        {
            LiveItems.WriteToSnapshot(loaded, destroy, ItemTransitionReason.ContainerDeposit);
            destroy.RemovalCause = cause;
        }
        item.NoteRemovalCause(cause);
        item.BeginColdStorageRetirement();
        List<ServerPlayer> told = new();
        foreach (ServerPlayer player in NetworkServer.Instance.ServerPlayers)
        {
            player.NearbyItems.Remove(item);
            if (player.KnownItems.Remove(item))
                told.Add(player);
            player.AcknowledgedWorldItems.Remove(item.NetId);
        }
        if (destroy != null)
            foreach (ServerPlayer player in told)
                NetworkServer.Instance.SendItemUpdate(destroy, player);
        ServerItemManager.Instance?.UnregisterHostWorldItem(item);
        if (item.Item?.InContainer is ItemMagazine magazine)
            magazine.RemoveItem(item.gameObject, false, false);
        InventoryIntegration.RevokeMembership(item.gameObject, item.NetId);
        StorageIntegration.MoveTo(item.Item, StorageMembership.None);
        UnityEngine.Object.Destroy(item.gameObject);
    }

    // Its tool is gone for good: an owned one to its owner's Lost and Found, any other loose where
    // the tool was (as a destroyed box's contents go: OI-CON-11).
    private static void Release(NetworkedItem item, LiveItems.LiveItem loaded, Vector3 at, ChangeCause<ItemRemovalReason> cause)
    {
        if (item.Item?.InContainer is ItemMagazine magazine)
            magazine.RemoveItem(item.gameObject, false, false);
        if (loaded.Owner != Guid.Empty && LostAndFoundServer.Collect(item, LostItemReason.ContainerCleared, "its tool was destroyed"))
            return;
        ItemUpdate drop = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
        if (drop == null)
            return;
        drop.ItemState = ItemState.Dropped;
        drop.PlayerId = 0;
        drop.PlayerHand = PlayerHand.None;
        drop.ItemPosition = at;
        drop.ItemRotation = Quaternion.identity;
        drop.WorldParentKind = ItemWorldParentKind.World;
        drop.MagazineNetId = 0;
        drop.MagazineSlot = -1;
        drop.AuthorityRevision = loaded.Revision;
        if (!LiveItems.TryApplyServerTransition(item, drop, ItemTransitionReason.HostLocalState, true, out string rejection,
                ItemChangeLog.Move(ItemMoveReason.UnloadedFromMagazine, detail: "its tool was destroyed: " + cause.Reason)))
        {
            Multiplayer.LogWarning($"Magazine: {loaded.PrefabName} (netId {loaded.NetId}) couldn't come out of its destroyed tool: {rejection}");
            return;
        }
        item.ApplyServerCanonicalSnapshot(drop);
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        NetworkServer.Instance.SendItemUpdate(drop);
    }

    private static void MoveBack(StoredItemRecord stored, Guid toolId, Guid owner, Home loaded, string why)
    {
        if (owner != Guid.Empty)
            ServerLedger.CommitStored(stored.PersistentItemId, stored.PrefabName, loaded, Home.Container(toolId, stored.Slot),
                ItemChangeLog.Move(ItemMoveReason.HostCorrection, detail: "a row of its tool again: " + why));
    }
}
