using System;
using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items;

internal sealed partial class ServerItemManager
{
    // What players bring, take back and leave with: the host's own recall, adopting items only a
    // client's game knew, a returning player's originals, and a departing player's held items.

    public bool RequestHostRecall(NetworkedItem item, int requestedSlot)
    {
        ServerPlayer hostPlayer = NetworkServer.Instance.LocalPlayer;
        if (hostPlayer == null)
            return false;
        byte localPlayerId = hostPlayer.PlayerId;
        bool storedInLostAndFound = NetworkedLostAndFoundManager.Contains(item.NetId);
        if (!storedInLostAndFound &&
            !AuthoritativeItemRegistry.TryPrepareRecall(item, hostPlayer, requestedSlot,
                item.AuthorityRevision, out _, out string prepareRejection))
        {
            PublishHostRecallRejected(item, localPlayerId, prepareRejection);
            return false;
        }
        if (!storedInLostAndFound &&
            !item.TryPrepareLocalRecall(0, requestedSlot, out string localFailure))
        {
            if (AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(item, out ItemUpdateData correction))
                item.ApplyServerCanonicalSnapshot(correction);
            item.CompleteLocalRecallPreparation(0, false);
            PublishHostRecallRejected(item, localPlayerId, localFailure);
            return false;
        }

        bool accepted = storedInLostAndFound
            ? NetworkedLostAndFoundManager.TryRetrieveForRecall(hostPlayer, item,
                item.AuthorityRevision, requestedSlot, out ItemUpdateData snapshot,
                out string rejectionReason)
            : AuthoritativeItemRegistry.TryCommitPreparedRecall(item, hostPlayer, requestedSlot,
                item.AuthorityRevision, out snapshot, out rejectionReason);
        if (!accepted)
        {
            if (!storedInLostAndFound)
            {
                if (AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(item, out ItemUpdateData correction))
                    item.ApplyServerCanonicalSnapshot(correction);
                item.CompleteLocalRecallPreparation(0, false);
            }
            PublishHostRecallRejected(item, localPlayerId, rejectionReason);
            return false;
        }

        if (!storedInLostAndFound)
        {
            item.ApplyServerCanonicalSnapshot(snapshot);
            item.CompleteLocalRecallPreparation(0, true);
        }
        NetworkServer.Instance.SendItemUpdatePacket(snapshot);
        return true;
    }

    private static void PublishHostRecallRejected(NetworkedItem item, byte localPlayerId,
        string rejectionReason)
    {
        DebugRuntime.Publish("item", "item.recall-rejected", DebugRuntimeSide.Server,
            DebugSeverity.Warning, "Item", item.NetId.ToString(), new()
            {
                ["requestingPlayerId"] = localPlayerId,
                ["rejectionReason"] = rejectionReason ?? string.Empty
            });
    }

    /// <param name="restoredOriginal">
    /// Set when the request was a fresh copy of an item the returning player already owns: the
    /// adoption is rejected and the caller must send this snapshot to the player after the
    /// rejection, so the original lands in the inventory slot the rejected copy vacates.
    /// </param>
    public bool TryAdoptClientItem(ServerPlayer player, ItemAdoptionRequestData request,
        out ushort assignedNetId, out string rejectionReason, out ItemUpdateData restoredOriginal)
    {
        assignedNetId = 0;
        rejectionReason = string.Empty;
        restoredOriginal = null;
        if (!NetworkLifecycle.Instance.IsHost() || player == null)
        {
            rejectionReason = "invalid-runtime-or-player";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.AdoptionToken) || request.AdoptionToken.Length > 64)
        {
            rejectionReason = "invalid-adoption-token";
            return false;
        }
        if (request.Snapshot == null || request.Snapshot.ItemNetId != 0)
        {
            rejectionReason = "invalid-adoption-snapshot";
            return false;
        }
        if (!TemporaryHostAdoptionCompatibility.AllowsHostRequest(request,
                NetworkedItemManager.IsJobDocumentPrefab, out rejectionReason))
            return false;
        ItemUpdateData.ItemUpdateType updateType = request.Snapshot.UpdateType;
        if ((updateType & ItemUpdateData.ItemUpdateType.FullSync) == 0 ||
            (updateType & (ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.Destroy)) != 0)
        {
            rejectionReason = "invalid-adoption-update-type";
            return false;
        }
        if (!IsFinite(request.Position) || !IsFinite(request.Rotation))
        {
            rejectionReason = "invalid-adoption-transform";
            return false;
        }
        if (request.Snapshot.ItemState is ItemState.Dropped or ItemState.Thrown &&
            (request.Position - player.AbsoluteWorldPosition).sqrMagnitude > 64f * 64f)
        {
            rejectionReason = "adoption-position-out-of-range";
            return false;
        }

        string token = request.AdoptionToken;
        if (TemporaryHostAdoptions.TryGetCompleted(player.PlayerId, token, out HostAdoptionOutcome previous))
        {
            assignedNetId = previous.AssignedNetId;
            rejectionReason = previous.RejectionReason;
            return previous.Accepted;
        }
        HostAdoptionBeginStatus begin = TemporaryHostAdoptions.TryBegin(player.PlayerId, token);
        if (begin != HostAdoptionBeginStatus.Started)
        {
            rejectionReason = begin == HostAdoptionBeginStatus.AlreadyPending
                ? "adoption-token-pending"
                : "invalid-adoption-token";
            return false;
        }
        if (TryRestoreReturningOwnerItem(player, request.PrefabName, out restoredOriginal))
        {
            assignedNetId = restoredOriginal.ItemNetId;
            rejectionReason = "returning-owner-original-restored:" + assignedNetId;
            TemporaryHostAdoptions.Complete(player.PlayerId, token, false, assignedNetId, rejectionReason);
            return false;
        }
        if (!shell.TryGetItemPrefab(request.PrefabName, out GameObject prefab))
        {
            rejectionReason = "unknown-prefab";
            TemporaryHostAdoptions.Complete(player.PlayerId, token, false, 0, rejectionReason);
            return false;
        }
        GameObject gameObject = Object.Instantiate(prefab, request.Position + WorldMover.currentMove, request.Rotation);
        NetworkedItem networkedItem = gameObject.GetOrAddComponent<NetworkedItem>();
        if (networkedItem == null || networkedItem.NetId == 0)
        {
            Object.Destroy(gameObject);
            rejectionReason = "authoritative-id-allocation-failed";
            TemporaryHostAdoptions.Complete(player.PlayerId, token, false, 0, rejectionReason);
            return false;
        }

        assignedNetId = networkedItem.NetId;
        player.KnownItems[networkedItem] = NetworkLifecycle.Instance.Tick;
        player.AcknowledgedWorldItems.Add(networkedItem.NetId);
        shell.ScheduleTrackedValueFinalization(networkedItem);
        request.Snapshot.ItemPosition = request.Position;
        request.Snapshot.ItemRotation = request.Rotation;
        networkedItem.ServerInitialiseAdoptedItem(player, request.Snapshot);
        TemporaryHostAdoptions.Complete(player.PlayerId, token, true, assignedNetId, string.Empty);
        return true;
    }

    // Originals already handed back to each player this session, so two fresh copies of the
    // same prefab never claim the same original.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServerPlayer, HashSet<ushort>>
        restoredOriginals = new();

    /// <summary>
    /// A returning player's game spawns a fresh starting kit and essentials and asks for each to
    /// be adopted, while the host still holds that player's originals (kept on disconnect, keyed
    /// to their identity). Adopting the copies duplicated every item, and the original
    /// essentials ended up on the floor because DV allows one of each. Instead, hand the
    /// original back: move it into the player's inventory with a new canonical revision.
    /// </summary>
    private static bool TryRestoreReturningOwnerItem(ServerPlayer player, string prefabName,
        out ItemUpdateData restored)
    {
        restored = null;
        if (player == null || player.Guid == Guid.Empty || string.IsNullOrEmpty(prefabName))
            return false;
        HashSet<ushort> alreadyRestored = restoredOriginals.GetOrCreateValue(player);

        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.Records.ToArray())
        {
            if (record.PersistentOwnerIdentity != player.Guid || alreadyRestored.Contains(record.NetId))
                continue;
            // Only items still with their owner; anything since picked up by someone else or
            // left in the world stays where it is.
            if (record.Placement is not (ItemPlacementKind.PlayerInventory or ItemPlacementKind.PlayerHand) ||
                record.PlacementPlayerIdentity != player.Guid && record.PlacementPlayerIdentity != Guid.Empty)
                continue;
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem original) || original == null ||
                original.Item?.InventorySpecs?.ItemPrefabName != prefabName)
                continue;

            ItemUpdateData snapshot = original.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
            if (snapshot == null)
                continue;
            snapshot.ItemState = ItemState.InInventory;
            snapshot.PlayerId = player.PlayerId;
            snapshot.PlayerHand = PlayerHand.None;
            snapshot.AuthorityRevision = record.Revision;
            if (!AuthoritativeItemRegistry.TryApplyTransition(original, snapshot, player,
                    ItemTransitionReason.PersistenceRestore, true, out string rejection,
                    cause: ItemChangeLog.Move(ItemMoveReason.ReturnedToRejoiningOwner, player,
                        "their game's fresh copy is not adopted")))
            {
                Multiplayer.LogWarning($"Could not restore item {record.NetId} ({prefabName}) to returning player {player.Username}: {rejection}");
                continue;
            }

            alreadyRestored.Add(record.NetId);
            original.ApplyServerCanonicalSnapshot(snapshot);
            player.KnownItems[original] = NetworkLifecycle.Instance.Tick;
            Multiplayer.Log($"Restored {prefabName} ({record.NetId}) to returning player {player.Username} instead of adopting a duplicate");
            restored = snapshot;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Host: a disconnecting player can't keep holding anything. Commit each item in their hand
    /// as dropped where it is, so the authority record matches the object lying in the world
    /// and anyone can pick it up. Their own inventory items stay keyed to them for when they
    /// return. Items in their hand or inventory that belong to someone else go to their owner's
    /// Lost and Found, whether the owner is online or not (owned-items.md, "Leaving").
    /// </summary>
    internal void DropHeldItemsOfDepartingPlayer(ServerPlayer player)
    {
        if (player == null || !NetworkLifecycle.Instance.IsHost())
            return;

        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.Records.ToArray())
        {
            if (record.PlacementPlayerId != player.PlayerId)
                continue;
            bool inHand = record.Placement == ItemPlacementKind.PlayerHand;
            bool borrowed = record.Placement == ItemPlacementKind.PlayerInventory && IsSomeoneElses(record, player);
            if (!inHand && !borrowed)
                continue;
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
                continue;

            ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
            if (snapshot == null)
                continue;
            snapshot.ItemState = ItemState.Dropped;
            snapshot.PlayerId = 0;
            snapshot.PlayerHand = PlayerHand.None;
            snapshot.ItemPosition = item.transform.position - WorldMover.currentMove;
            snapshot.ItemRotation = item.transform.rotation;
            snapshot.AuthorityRevision = record.Revision;
            if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, player,
                    ItemTransitionReason.HostLocalState, true, out string rejection,
                    cause: ItemChangeLog.Move(ItemMoveReason.DroppedOnDisconnect, player,
                        inHand ? "was in their hand" : "borrowed, owner " +
                            (ItemChangeLog.PlayerName(record.PersistentOwnerPlayerId) ?? "P" + record.PersistentOwnerPlayerId))))
            {
                Multiplayer.LogWarning($"Could not drop item {record.NetId} held by departing player {player.Username}: {rejection}");
                continue;
            }
            item.ApplyServerCanonicalSnapshot(snapshot);
            RegisterHostWorldItem(item);
            NetworkServer.Instance.SendItemUpdatePacket(snapshot, excludePlayer: player);
            // Someone else's item goes straight to its owner's Lost and Found, online or not, rather
            // than lie where the borrower stood (owned-items.md, "Leaving").
            if (IsSomeoneElses(record, player))
                NetworkedLostAndFoundManager.Collect(item, LostItemReason.BorrowerLeft, "left by " + player.Username);
        }
    }

    private static bool IsSomeoneElses(AuthoritativeItemRegistry.Record record, ServerPlayer player) =>
        record.PersistentOwnerPlayerId != 0 && record.PersistentOwnerPlayerId != player.PlayerId &&
        record.PersistentOwnerIdentity != player.Guid;

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static bool IsFinite(Quaternion value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
        !float.IsNaN(value.w) && !float.IsInfinity(value.w);

}
