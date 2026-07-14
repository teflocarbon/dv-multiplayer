using DV.InventorySystem;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Items;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Host-owned source of truth for item identity, placement, persistent ownership and
/// inventory claims. Unity object fields are projections of these records, never peers.
/// </summary>
public static class AuthoritativeItemRegistry
{
    public sealed class Record
    {
        public ushort NetId;
        public uint Revision;
        public ItemPlacementKind Placement;
        public byte PlacementPlayerId;
        public byte PersistentOwnerPlayerId;
        public byte InventoryClaimPlayerId;
        public int InventoryClaimSlot = -1;
        public ItemInventoryClaimFlags InventoryClaimFlags;
        public string PrefabName = string.Empty;
        public Vector3 Position;
        public Quaternion Rotation;
        public ItemTransitionReason LastReason;
    }

    private static readonly Dictionary<ushort, Record> records = new();

    public static bool TryGet(ushort netId, out Record record) => records.TryGetValue(netId, out record);

    public static Record Ensure(NetworkedItem item, ServerPlayer actor = null, ItemUpdateData seed = null)
    {
        if (item == null || item.NetId == 0)
            return null;
        if (records.TryGetValue(item.NetId, out Record existing))
            return existing;

        bool persistentlyPlayerOwned = item.Item?.IsEssential() == true ||
            item.Item?.InventorySpecs?.BelongsToPlayer == true;
        byte claimedOwner = seed?.PersistentOwnerPlayerId ?? 0;
        byte persistentOwner = actor != null && persistentlyPlayerOwned &&
            (claimedOwner == 0 || claimedOwner == actor.PlayerId)
                ? actor.PlayerId
                : (byte)0;

        Record record = new()
        {
            NetId = item.NetId,
            Placement = PlacementFrom(seed?.ItemState ?? item.DebugCurrentState),
            PlacementPlayerId = seed?.PlayerId ?? 0,
            PersistentOwnerPlayerId = persistentOwner,
            InventoryClaimPlayerId = seed?.InventoryClaimPlayerId ?? 0,
            InventoryClaimSlot = seed?.InventoryClaimSlot ?? -1,
            InventoryClaimFlags = seed?.InventoryClaimFlags ?? ItemInventoryClaimFlags.None,
            PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
            Position = seed?.ItemPosition ?? item.transform.position - WorldMover.currentMove,
            Rotation = seed?.ItemRotation ?? item.transform.rotation,
            LastReason = ItemTransitionReason.InitialRegistration
        };
        records[item.NetId] = record;
        if (persistentOwner != 0 && actor?.PlayerId == persistentOwner)
            actor.AddOwnedItem(item.NetId);
        Publish("item.canonical-record-created", item, record, null);
        return record;
    }

    public static bool TryApplyTransition(NetworkedItem item, ItemUpdateData snapshot, ServerPlayer actor,
        ItemTransitionReason reason, bool force, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (item == null || snapshot == null || actor == null || item.NetId == 0)
        {
            rejectionReason = "invalid-transition-input";
            return false;
        }

        Record record = Ensure(item, actor, snapshot);
        Publish("item.transition-requested", item, record, new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["requestedState"] = snapshot.ItemState.ToString(),
            ["requestedRevision"] = snapshot.AuthorityRevision,
            ["force"] = force,
            ["reason"] = reason.ToString()
        });

        ItemUpdateData.ItemUpdateType placementFlags = ItemUpdateData.ItemUpdateType.Create |
            ItemUpdateData.ItemUpdateType.ItemState | ItemUpdateData.ItemUpdateType.FullSync;
        bool appliesPlacement = (snapshot.UpdateType & placementFlags) != 0;
        ItemPlacementKind requestedPlacement = appliesPlacement ? PlacementFrom(snapshot.ItemState) : record.Placement;
        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(ToCore(record), new ItemTransitionCommand
        {
            ActorPlayerId = actor.PlayerId,
            ExpectedRevision = snapshot.AuthorityRevision,
            Force = force,
            AppliesPlacement = appliesPlacement,
            RequestedPlacement = (AuthorityPlacement)(byte)requestedPlacement,
            IsEssential = item.Item?.IsEssential() == true,
            InventoryClaimSlot = snapshot.InventoryClaimSlot,
            InventoryClaimFlags = (AuthorityClaimFlags)(byte)snapshot.InventoryClaimFlags
        });
        if (!result.Accepted)
        {
            rejectionReason = result.RejectionReason;
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }

        if (appliesPlacement && item.Item?.IsEssential() == true && snapshot.InventoryClaimSlot >= 0 &&
            record.InventoryClaimSlot >= 0 && record.InventoryClaimSlot != snapshot.InventoryClaimSlot &&
            record.PersistentOwnerPlayerId == actor.PlayerId)
        {
            Publish("item.essential-claim-slot-move-rejected", item, record, new()
            {
                ["requestedSlot"] = snapshot.InventoryClaimSlot,
                ["retainedSlot"] = record.InventoryClaimSlot
            }, DebugSeverity.Warning);
        }

        ApplyCore(record, result.State);
        record.Position = snapshot.ItemPosition;
        record.Rotation = snapshot.ItemRotation;
        record.LastReason = reason;
        WriteToSnapshot(record, snapshot, reason);
        Publish("item.transition-accepted", item, record, new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["reason"] = reason.ToString()
        });
        return true;
    }

    public static bool TryRecall(NetworkedItem item, ServerPlayer requester, int requestedSlot,
        uint expectedRevision, out ItemUpdateData snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (item == null || requester == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out Record record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        ItemAuthorityResult result = ItemAuthorityStateMachine.Recall(ToCore(record), new ItemRecallCommand
        {
            RequestingPlayerId = requester.PlayerId,
            ExpectedRevision = expectedRevision,
            RequestedSlot = requestedSlot,
            IsEssential = item.Item?.IsEssential() == true
        });
        if (!result.Accepted)
        {
            rejectionReason = result.RejectionReason;
            return false;
        }

        ItemUpdateData recallSnapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
        if (recallSnapshot == null)
        {
            rejectionReason = "snapshot-creation-failed";
            return false;
        }

        byte previousPossessor = record.PlacementPlayerId;
        ApplyCore(record, result.State);
        record.LastReason = ItemTransitionReason.OwnerRecall;

        snapshot = recallSnapshot;
        snapshot.ItemState = ItemState.InInventory;
        snapshot.PlayerId = requester.PlayerId;
        WriteToSnapshot(record, snapshot, ItemTransitionReason.OwnerRecall);
        Publish("item.recall-accepted", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["previousPossessorPlayerId"] = previousPossessor,
            ["requestedSlot"] = requestedSlot
        });
        return true;
    }

    public static void ApplyReplicaMetadata(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (item == null || snapshot == null || item.NetId == 0)
            return;
        item.ApplyAuthorityMetadata(snapshot);
    }

    public static void WriteToSnapshot(Record record, ItemUpdateData snapshot, ItemTransitionReason reason)
    {
        snapshot.AuthorityRevision = record.Revision;
        snapshot.PersistentOwnerPlayerId = record.PersistentOwnerPlayerId;
        snapshot.InventoryClaimPlayerId = record.InventoryClaimPlayerId;
        snapshot.InventoryClaimSlot = record.InventoryClaimSlot;
        snapshot.InventoryClaimFlags = record.InventoryClaimFlags;
        snapshot.TransitionReason = reason;
        snapshot.PlayerId = record.PlacementPlayerId;
    }

    public static Dictionary<string, object> Snapshot(Record record)
    {
        if (record == null)
            return new();
        return new Dictionary<string, object>
        {
            ["netId"] = record.NetId,
            ["revision"] = record.Revision,
            ["placement"] = record.Placement.ToString(),
            ["placementPlayerId"] = record.PlacementPlayerId,
            ["persistentOwnerPlayerId"] = record.PersistentOwnerPlayerId,
            ["inventoryClaimPlayerId"] = record.InventoryClaimPlayerId,
            ["inventoryClaimSlot"] = record.InventoryClaimSlot,
            ["inventoryClaimFlags"] = record.InventoryClaimFlags.ToString(),
            ["prefabName"] = record.PrefabName,
            ["lastReason"] = record.LastReason.ToString()
        };
    }

    public static void Remove(ushort netId) => records.Remove(netId);
    public static void Clear() => records.Clear();

    private static ItemAuthorityState ToCore(Record record)
    {
        return new ItemAuthorityState
        {
            NetId = record.NetId,
            Revision = record.Revision,
            Placement = (AuthorityPlacement)(byte)record.Placement,
            PlacementPlayerId = record.PlacementPlayerId,
            PersistentOwnerPlayerId = record.PersistentOwnerPlayerId,
            InventoryClaimPlayerId = record.InventoryClaimPlayerId,
            InventoryClaimSlot = record.InventoryClaimSlot,
            InventoryClaimFlags = (AuthorityClaimFlags)(byte)record.InventoryClaimFlags
        };
    }

    private static void ApplyCore(Record record, ItemAuthorityState state)
    {
        record.Revision = state.Revision;
        record.Placement = (ItemPlacementKind)(byte)state.Placement;
        record.PlacementPlayerId = state.PlacementPlayerId;
        record.PersistentOwnerPlayerId = state.PersistentOwnerPlayerId;
        record.InventoryClaimPlayerId = state.InventoryClaimPlayerId;
        record.InventoryClaimSlot = state.InventoryClaimSlot;
        record.InventoryClaimFlags = (ItemInventoryClaimFlags)(byte)state.InventoryClaimFlags;
    }

    private static ItemPlacementKind PlacementFrom(ItemState state) => state switch
    {
        ItemState.InHand => ItemPlacementKind.PlayerHand,
        ItemState.InInventory => ItemPlacementKind.PlayerInventory,
        ItemState.Attached => ItemPlacementKind.Attached,
        _ => ItemPlacementKind.World
    };

    private static void PublishRejected(NetworkedItem item, Record record, ServerPlayer actor,
        ItemUpdateData snapshot, string reason)
    {
        Publish("item.transition-rejected", item, record, new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["requestedState"] = snapshot.ItemState.ToString(),
            ["requestedRevision"] = snapshot.AuthorityRevision,
            ["rejectionReason"] = reason
        }, DebugSeverity.Warning);
    }

    private static void Publish(string eventName, NetworkedItem item, Record record,
        Dictionary<string, object> additional, DebugSeverity severity = DebugSeverity.Info)
    {
        if (!DebugRuntime.EnabledFor("item") || item == null)
            return;
        Dictionary<string, object> data = Snapshot(record);
        if (additional != null)
            foreach (KeyValuePair<string, object> pair in additional)
                data[pair.Key] = pair.Value;
        DebugRuntime.Publish("item", eventName, DebugRuntimeSide.Server, severity,
            "Item", item.NetId.ToString(), data);
    }
}
