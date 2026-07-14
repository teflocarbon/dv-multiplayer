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

        ItemInventoryClaimFlags claimFlags = seed?.InventoryClaimFlags ?? ItemInventoryClaimFlags.None;
        bool hasRetrievalClaim = (seed?.InventoryClaimSlot ?? -1) >= 0 &&
            HasRetrievalFlag(claimFlags);
        byte claimedOwner = seed?.PersistentOwnerPlayerId ?? 0;
        bool actorOwnsPlayerItem = item.Item?.InventorySpecs?.BelongsToPlayer == true && actor != null &&
            (claimedOwner == actor.PlayerId ||
             claimedOwner == 0 && item.Item?.InventorySpecs?.BelongsToPlayer == true);
        byte persistentOwner = actorOwnsPlayerItem ? actor.PlayerId : (byte)0;

        Record record = new()
        {
            NetId = item.NetId,
            Placement = PlacementFrom(seed?.ItemState ?? item.DebugCurrentState),
            PlacementPlayerId = seed?.PlayerId ?? 0,
            PersistentOwnerPlayerId = persistentOwner,
            InventoryClaimPlayerId = persistentOwner == 0 || !hasRetrievalClaim ? (byte)0 :
                seed?.InventoryClaimPlayerId ?? persistentOwner,
            InventoryClaimSlot = persistentOwner == 0 || !hasRetrievalClaim ? -1 : seed?.InventoryClaimSlot ?? -1,
            InventoryClaimFlags = persistentOwner == 0 || !hasRetrievalClaim ? ItemInventoryClaimFlags.None : claimFlags,
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
        ItemTransitionReason reason, bool force, out string rejectionReason,
        bool clearRetrievalClaim = false)
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
            ClearRetrievalClaim = clearRetrievalClaim,
            MayEstablishPersistentOwner = item.Item?.InventorySpecs?.BelongsToPlayer == true,
            InventoryClaimSlot = snapshot.InventoryClaimSlot,
            InventoryClaimFlags = (AuthorityClaimFlags)(byte)snapshot.InventoryClaimFlags
        });
        if (!result.Accepted)
        {
            rejectionReason = result.RejectionReason;
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }

        if (appliesPlacement && HasRetrievalFlag(record.InventoryClaimFlags) &&
            snapshot.InventoryClaimSlot >= 0 &&
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
            RequestedSlot = requestedSlot
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

    public static bool TryMoveToLostAndFound(NetworkedItem item, out Record record,
        out string rejectionReason, bool allowRecoveryPlacement = false)
    {
        record = null;
        rejectionReason = string.Empty;
        if (item == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (record.PersistentOwnerPlayerId == 0)
        {
            rejectionReason = "item-has-no-persistent-owner";
            return false;
        }
        if (record.Placement != ItemPlacementKind.World &&
            (!allowRecoveryPlacement || record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory))
        {
            rejectionReason = "item-not-in-world";
            return false;
        }
        if (record.Revision == uint.MaxValue)
        {
            rejectionReason = "authority-revision-exhausted";
            return false;
        }

        record.Revision++;
        record.Placement = ItemPlacementKind.LostAndFound;
        record.PlacementPlayerId = 0;
        record.LastReason = ItemTransitionReason.LostAndFoundCollection;
        Publish("item.lost-and-found-authority-applied", item, record, null);
        return true;
    }

    public static bool TryRetrieveFromLostAndFound(NetworkedItem item, ServerPlayer requester,
        uint expectedRevision, int targetSlot, out ItemUpdateData snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (item == null || requester == null || item.NetId == 0 ||
            !records.TryGetValue(item.NetId, out Record record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (record.Placement != ItemPlacementKind.LostAndFound)
        {
            rejectionReason = "item-not-in-lost-and-found";
            return false;
        }
        if (record.PersistentOwnerPlayerId != requester.PlayerId)
        {
            rejectionReason = "requester-not-persistent-owner";
            return false;
        }
        if (record.Revision != expectedRevision)
        {
            rejectionReason = "stale-lost-item-revision";
            return false;
        }
        if (record.Revision == uint.MaxValue)
        {
            rejectionReason = "authority-revision-exhausted";
            return false;
        }

        ItemUpdateData created = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);
        if (created == null)
        {
            rejectionReason = "snapshot-creation-failed";
            return false;
        }
        record.Revision++;
        record.Placement = ItemPlacementKind.PlayerInventory;
        record.PlacementPlayerId = requester.PlayerId;
        record.InventoryClaimPlayerId = requester.PlayerId;
        record.InventoryClaimSlot = targetSlot;
        record.InventoryClaimFlags &= ~(ItemInventoryClaimFlags.Dropped | ItemInventoryClaimFlags.Stolen);
        record.LastReason = ItemTransitionReason.LostAndFoundRetrieval;

        created.ItemState = ItemState.InInventory;
        created.PlayerId = requester.PlayerId;
        WriteToSnapshot(record, created, ItemTransitionReason.LostAndFoundRetrieval);
        snapshot = created;
        Publish("item.lost-and-found-retrieval-authority-applied", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["targetSlot"] = targetSlot
        });
        return true;
    }

    public static Record RestoreLostRecord(NetworkedItem item, byte ownerPlayerId, uint revision,
        string prefabName, byte inventoryClaimPlayerId = 0, int inventoryClaimSlot = -1,
        ItemInventoryClaimFlags inventoryClaimFlags = ItemInventoryClaimFlags.None)
    {
        if (item == null || item.NetId == 0 || ownerPlayerId == 0)
            return null;
        Record record = Ensure(item);
        record.Revision = revision;
        record.Placement = ItemPlacementKind.LostAndFound;
        record.PlacementPlayerId = 0;
        record.PersistentOwnerPlayerId = ownerPlayerId;
        record.InventoryClaimPlayerId = inventoryClaimPlayerId;
        record.InventoryClaimSlot = inventoryClaimSlot;
        record.InventoryClaimFlags = inventoryClaimFlags;
        record.PrefabName = string.IsNullOrWhiteSpace(prefabName) ? record.PrefabName : prefabName;
        record.LastReason = ItemTransitionReason.LostAndFoundCollection;
        if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(ownerPlayerId, out ServerPlayer owner))
            owner.AddOwnedItem(item.NetId);
        item.ApplyAuthorityMetadata(new ItemUpdateData
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            PersistentOwnerPlayerId = ownerPlayerId,
            InventoryClaimPlayerId = record.InventoryClaimPlayerId,
            InventoryClaimSlot = record.InventoryClaimSlot,
            InventoryClaimFlags = record.InventoryClaimFlags,
            TransitionReason = record.LastReason
        });
        return record;
    }

    public static void RollbackLostAndFoundCollection(NetworkedItem item, uint previousRevision,
        ItemPlacementKind previousPlacement, byte previousPlacementPlayerId,
        ItemTransitionReason previousReason)
    {
        if (item == null || !records.TryGetValue(item.NetId, out Record record) ||
            record.Placement != ItemPlacementKind.LostAndFound)
            return;
        record.Revision = previousRevision;
        record.Placement = previousPlacement;
        record.PlacementPlayerId = previousPlacementPlayerId;
        record.LastReason = previousReason;
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
    public static IEnumerable<Record> Records => records.Values;

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

    private static bool HasRetrievalFlag(ItemInventoryClaimFlags flags) =>
        (flags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;

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
