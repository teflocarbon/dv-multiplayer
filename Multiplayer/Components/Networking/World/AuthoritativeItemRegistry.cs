using DV.InventorySystem;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Items;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Components.Networking.Train;
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
        public Guid PlacementPlayerIdentity;
        public byte PersistentOwnerPlayerId;
        public Guid PersistentOwnerIdentity;
        public byte InventoryClaimPlayerId;
        public int InventoryClaimSlot = -1;
        public ItemInventoryClaimFlags InventoryClaimFlags;
        public string PrefabName = string.Empty;
        public Vector3 Position;
        public Quaternion Rotation;
        public ItemTransitionReason LastReason;
        public Guid PersistentItemId;
        public string AuthoredItemKey = string.Empty;
        public ItemPlacementKind BaselinePlacement;
        public Vector3 BaselinePosition;
        public Quaternion BaselineRotation;
        public ItemWorldParentKind WorldParentKind;
        public ushort WorldParentNetId;
        public string WorldParentKey = string.Empty;
        public string WorldParentPersistentId = string.Empty;
        public Vector3 ParentLocalPosition;
        public Quaternion ParentLocalRotation;
        public bool HasPersistentOverride;
        public bool IsReplenishableStockFork;
        public ushort AttachedCarNetId;
        public string AttachedCarPersistentId = string.Empty;
        public bool AttachedFront;
    }

    private static readonly Dictionary<ushort, Record> records = new();

    public static bool TryGet(ushort netId, out Record record) => records.TryGetValue(netId, out record);
    public static IEnumerable<Record> GetAll() => records.Values;

    public static void MarkAuthoredIdentity(NetworkedItem item)
    {
        if (item == null || !item.IsSceneAuthored || !records.TryGetValue(item.NetId, out Record record))
            return;
        record.AuthoredItemKey = item.AuthoredItemKey;
        record.PersistentItemId = Guid.Empty;
        PersistentWorldItemIdentity identity = item.GetComponent<PersistentWorldItemIdentity>();
        if (identity != null)
            UnityEngine.Object.Destroy(identity);
    }

    public static Guid PromoteAuthoredToPersistent(NetworkedItem item,
        bool replenishableStockFork = false)
    {
        if (item == null || !records.TryGetValue(item.NetId, out Record record))
            return Guid.Empty;
        PersistentWorldItemIdentity identity = item.GetComponent<PersistentWorldItemIdentity>() ??
                                               item.gameObject.AddComponent<PersistentWorldItemIdentity>();
        Guid persistentId = identity.Ensure();
        record.PersistentItemId = persistentId;
        record.AuthoredItemKey = string.Empty;
        record.HasPersistentOverride = true;
        record.IsReplenishableStockFork |= replenishableStockFork;
        return persistentId;
    }

    public static Record Ensure(NetworkedItem item, ServerPlayer actor = null, ItemUpdateData seed = null)
    {
        if (item == null || item.NetId == 0)
            return null;
        if (records.TryGetValue(item.NetId, out Record existing))
            return existing;

        ItemInventoryClaimFlags claimFlags = seed?.InventoryClaimFlags ?? ItemInventoryClaimFlags.None;
        bool hasRetrievalClaim = item.Item?.InventorySpecs?.IsEssential == true &&
            (seed?.InventoryClaimSlot ?? -1) >= 0 &&
            HasRetrievalFlag(claimFlags);
        byte claimedOwner = seed?.PersistentOwnerPlayerId ?? 0;
        bool actorOwnsPlayerItem = item.Item?.InventorySpecs?.BelongsToPlayer == true && actor != null &&
            (claimedOwner == actor.PlayerId ||
             claimedOwner == 0 && item.Item?.InventorySpecs?.BelongsToPlayer == true);
        byte persistentOwner = actorOwnsPlayerItem ? actor.PlayerId : (byte)0;

        PersistentWorldItemIdentity persistentIdentity = item.GetComponent<PersistentWorldItemIdentity>();
        Guid persistentItemId = item.IsSceneAuthored ? Guid.Empty :
            (persistentIdentity ?? item.gameObject.AddComponent<PersistentWorldItemIdentity>()).Ensure();
        ItemPlacementKind initialPlacement = PlacementFrom(seed?.ItemState ?? item.DebugCurrentState);
        Vector3 initialPosition = seed?.ItemPosition ?? item.transform.position - WorldMover.currentMove;
        Quaternion initialRotation = seed?.ItemRotation ?? item.transform.rotation;
        Record record = new()
        {
            NetId = item.NetId,
            Placement = initialPlacement,
            PlacementPlayerId = seed?.PlayerId ?? 0,
            PlacementPlayerIdentity = seed?.PlayerId != 0 && actor?.PlayerId == seed.PlayerId
                ? actor.Guid : Guid.Empty,
            PersistentOwnerPlayerId = persistentOwner,
            PersistentOwnerIdentity = persistentOwner == 0 ? Guid.Empty : actor?.Guid ?? Guid.Empty,
            InventoryClaimPlayerId = persistentOwner == 0 || !hasRetrievalClaim ? (byte)0 :
                seed?.InventoryClaimPlayerId ?? persistentOwner,
            InventoryClaimSlot = persistentOwner == 0 || !hasRetrievalClaim ? -1 : seed?.InventoryClaimSlot ?? -1,
            InventoryClaimFlags = persistentOwner == 0 || !hasRetrievalClaim ? ItemInventoryClaimFlags.None : claimFlags,
            PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
            Position = initialPosition,
            Rotation = initialRotation,
            LastReason = ItemTransitionReason.InitialRegistration,
            PersistentItemId = persistentItemId,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty,
            BaselinePlacement = initialPlacement,
            BaselinePosition = initialPosition,
            BaselineRotation = initialRotation,
            WorldParentKind = seed?.WorldParentKind ?? ItemWorldParentKind.World,
            WorldParentNetId = seed?.WorldParentNetId ?? 0,
            WorldParentKey = seed?.WorldParentKey ?? string.Empty,
            ParentLocalPosition = seed?.ParentLocalPosition ?? Vector3.zero,
            ParentLocalRotation = seed?.ParentLocalRotation ?? Quaternion.identity,
            AttachedCarNetId = seed?.CarNetId ?? 0,
            AttachedFront = seed?.AttachedFront ?? true
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
        bool retrievalEligible = item.Item?.InventorySpecs?.IsEssential == true;
        ItemPlacementKind previousPlacement = record.Placement;
        ItemAuthorityState currentState = ToCore(record);
        if (!retrievalEligible)
        {
            // Sanitize the candidate state, not the live record. A rejected transition
            // must never mutate canonical authority; the cleanup commits only when the
            // transition itself is accepted.
            currentState.InventoryClaimPlayerId = 0;
            currentState.InventoryClaimSlot = -1;
            currentState.InventoryClaimFlags = AuthorityClaimFlags.None;
        }
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
        if (appliesPlacement && snapshot.ItemState is ItemState.Dropped or ItemState.Thrown)
            requestedPlacement = snapshot.WorldParentKind switch
            {
                ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
                ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
                _ => requestedPlacement
            };
        byte previousPersistentOwner = record.PersistentOwnerPlayerId;
        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(currentState, new ItemTransitionCommand
        {
            ActorPlayerId = actor.PlayerId,
            ExpectedRevision = snapshot.AuthorityRevision,
            Force = force,
            AppliesPlacement = appliesPlacement,
            RequestedPlacement = (AuthorityPlacement)(byte)requestedPlacement,
            ClearRetrievalClaim = clearRetrievalClaim,
            EstablishPersistentOwner = reason == ItemTransitionReason.ClientAdoption,
            MayEstablishPersistentOwner = item.Item?.InventorySpecs?.BelongsToPlayer == true,
            InventoryClaimSlot = retrievalEligible ? snapshot.InventoryClaimSlot : -1,
            InventoryClaimFlags = retrievalEligible
                ? (AuthorityClaimFlags)(byte)snapshot.InventoryClaimFlags
                : AuthorityClaimFlags.None
        });
        if (!result.Accepted)
        {
            rejectionReason = result.RejectionReason;
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }

        if (appliesPlacement && HasRetrievalFlag((ItemInventoryClaimFlags)(byte)result.State.InventoryClaimFlags) &&
            snapshot.InventoryClaimSlot >= 0 &&
            result.State.InventoryClaimSlot >= 0 &&
            result.State.InventoryClaimSlot != snapshot.InventoryClaimSlot &&
            record.PersistentOwnerPlayerId == actor.PlayerId)
        {
            Publish("item.essential-claim-slot-move-rejected", item, record, new()
            {
                ["requestedSlot"] = snapshot.InventoryClaimSlot,
                ["retainedSlot"] = result.State.InventoryClaimSlot
            }, DebugSeverity.Warning);
        }

        ApplyCore(record, result.State);
        record.PlacementPlayerIdentity = record.PlacementPlayerId == actor.PlayerId
            ? actor.Guid : Guid.Empty;
        if (record.PersistentOwnerPlayerId == actor.PlayerId && record.PersistentOwnerIdentity == Guid.Empty)
            record.PersistentOwnerIdentity = actor.Guid;
        if (previousPersistentOwner == 0 && record.PersistentOwnerPlayerId == actor.PlayerId)
            actor.AddOwnedItem(item.NetId);
        record.Position = snapshot.ItemPosition;
        record.Rotation = snapshot.ItemRotation;
        record.WorldParentKind = snapshot.WorldParentKind;
        record.WorldParentNetId = snapshot.WorldParentNetId;
        record.WorldParentKey = snapshot.WorldParentKey ?? string.Empty;
        if (record.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(record.WorldParentNetId, out TrainCar parentCar))
            record.WorldParentPersistentId = parentCar.CarGUID ?? string.Empty;
        else if (record.WorldParentKind != ItemWorldParentKind.TrainInterior)
            record.WorldParentPersistentId = string.Empty;
        record.ParentLocalPosition = snapshot.ParentLocalPosition;
        record.ParentLocalRotation = snapshot.ParentLocalRotation;
        record.AttachedCarNetId = snapshot.CarNetId;
        record.AttachedFront = snapshot.AttachedFront;
        if (record.Placement == ItemPlacementKind.Attached &&
            NetworkedTrainCar.TryGet(record.AttachedCarNetId, out TrainCar attachedCar))
            record.AttachedCarPersistentId = attachedCar.CarGUID ?? string.Empty;
        if (item.IsSceneAuthored && snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState) &&
            snapshot.States is { Count: > 0 })
            record.HasPersistentOverride = true;
        record.LastReason = reason;
        WriteToSnapshot(record, snapshot, reason);
        Publish("item.transition-accepted", item, record, new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["reason"] = reason.ToString()
        });
        NetworkedItemManager.Instance?.OnAuthoritativeItemTransition(item, snapshot, record, actor,
            previousPlacement, appliesPlacement);
        return true;
    }

    public static bool TryCommitSpatialPose(NetworkedItem item, uint expectedRevision,
        ItemSpatialStateData state, out Record record, out string rejectionReason)
    {
        record = null;
        rejectionReason = string.Empty;
        if (item == null || state == null || item.NetId == 0 || state.ItemNetId != item.NetId ||
            !records.TryGetValue(item.NetId, out record))
        {
            rejectionReason = "invalid-spatial-commit-input";
            return false;
        }
        if (record.Revision != expectedRevision || state.AuthorityRevision != expectedRevision)
        {
            rejectionReason = "stale-spatial-authority-revision";
            return false;
        }
        if (record.Placement is not (ItemPlacementKind.World or ItemPlacementKind.TrainInterior or
            ItemPlacementKind.StaticParent))
        {
            rejectionReason = "spatial-commit-placement-changed";
            return false;
        }
        if (record.Revision == uint.MaxValue)
        {
            rejectionReason = "spatial-authority-revision-exhausted";
            return false;
        }

        record.Revision++;
        record.Placement = state.WorldParentKind switch
        {
            ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
            ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
            _ => ItemPlacementKind.World
        };
        record.PlacementPlayerId = 0;
        record.PlacementPlayerIdentity = Guid.Empty;
        record.Position = state.AbsolutePosition;
        record.Rotation = state.Rotation;
        record.WorldParentKind = state.WorldParentKind;
        record.WorldParentNetId = state.WorldParentNetId;
        record.WorldParentKey = state.WorldParentKey ?? string.Empty;
        record.ParentLocalPosition = state.ParentLocalPosition;
        record.ParentLocalRotation = state.ParentLocalRotation;
        if (record.WorldParentKind == ItemWorldParentKind.TrainInterior &&
            NetworkedTrainCar.TryGet(record.WorldParentNetId, out TrainCar parentCar))
            record.WorldParentPersistentId = parentCar.CarGUID ?? string.Empty;
        else if (record.WorldParentKind != ItemWorldParentKind.TrainInterior)
            record.WorldParentPersistentId = string.Empty;
        record.HasPersistentOverride = true;
        record.LastReason = ItemTransitionReason.SpatialSettlement;
        item.ApplyAuthorityMetadata(new ItemUpdateData
        {
            ItemNetId = item.NetId,
            AuthorityRevision = record.Revision,
            PersistentOwnerPlayerId = record.PersistentOwnerPlayerId,
            InventoryClaimPlayerId = record.InventoryClaimPlayerId,
            InventoryClaimSlot = record.InventoryClaimSlot,
            InventoryClaimFlags = record.InventoryClaimFlags,
            TransitionReason = record.LastReason
        });
        Publish("item.spatial-pose-committed", item, record, new()
        {
            ["simulationEpoch"] = state.SimulationEpoch,
            ["sampleSequence"] = state.SampleSequence,
            ["worldParentKind"] = state.WorldParentKind.ToString()
        });
        return true;
    }

    public static bool TryPrepareRecall(NetworkedItem item, ServerPlayer requester, int requestedSlot,
        uint expectedRevision, out ItemUpdateData snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (item == null || requester == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out Record record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (item.Item?.InventorySpecs?.IsEssential != true)
        {
            rejectionReason = "item-not-recallable";
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

        snapshot = recallSnapshot;
        snapshot.ItemState = ItemState.InInventory;
        snapshot.PlayerId = requester.PlayerId;
        WriteStateToSnapshot(result.State, snapshot, ItemTransitionReason.OwnerRecall);
        Publish("item.recall-prepared", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["requestedSlot"] = requestedSlot,
            ["baseRevision"] = record.Revision,
            ["preparedRevision"] = result.State.Revision
        });
        return true;
    }

    public static bool TryCommitPreparedRecall(NetworkedItem item, ServerPlayer requester,
        int requestedSlot, uint expectedRevision, out ItemUpdateData snapshot,
        out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (!TryPrepareRecall(item, requester, requestedSlot, expectedRevision,
                out ItemUpdateData prepared, out rejectionReason))
            return false;
        if (!records.TryGetValue(item.NetId, out Record record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }

        ItemAuthorityResult result = ItemAuthorityStateMachine.Recall(ToCore(record),
            new ItemRecallCommand
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

        byte previousPossessor = record.PlacementPlayerId;
        ApplyCore(record, result.State);
        record.LastReason = ItemTransitionReason.OwnerRecall;
        snapshot = prepared;
        WriteToSnapshot(record, snapshot, ItemTransitionReason.OwnerRecall);
        Publish("item.recall-accepted", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["previousPossessorPlayerId"] = previousPossessor,
            ["requestedSlot"] = requestedSlot,
            ["baseRevision"] = expectedRevision
        });
        return true;
    }

    public static bool TryRecall(NetworkedItem item, ServerPlayer requester, int requestedSlot,
        uint expectedRevision, out ItemUpdateData snapshot, out string rejectionReason) =>
        TryCommitPreparedRecall(item, requester, requestedSlot, expectedRevision,
            out snapshot, out rejectionReason);

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
        record.PlacementPlayerIdentity = Guid.Empty;
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
        record.PlacementPlayerIdentity = requester.Guid;
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
        record.PlacementPlayerIdentity = Guid.Empty;
        record.PersistentOwnerPlayerId = ownerPlayerId;
        record.InventoryClaimPlayerId = inventoryClaimPlayerId;
        record.InventoryClaimSlot = inventoryClaimSlot;
        record.InventoryClaimFlags = inventoryClaimFlags;
        record.PrefabName = string.IsNullOrWhiteSpace(prefabName) ? record.PrefabName : prefabName;
        record.LastReason = ItemTransitionReason.LostAndFoundCollection;
        if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(ownerPlayerId, out ServerPlayer owner))
        {
            record.PersistentOwnerIdentity = owner.Guid;
            owner.AddOwnedItem(item.NetId);
        }
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

    /// <summary>
    /// Registers an item materialized from host-owned detached storage without allowing
    /// the current holder to accidentally replace its persistent owner.
    /// </summary>
    public static Record RegisterMaterializedColdItem(NetworkedItem item, ServerPlayer holder,
        byte persistentOwnerPlayerId, ItemUpdateData snapshot)
    {
        if (item == null || holder == null || snapshot == null || item.NetId == 0)
            return null;

        Record record = new()
        {
            NetId = item.NetId,
            Revision = 1,
            Placement = ItemPlacementKind.PlayerInventory,
            PlacementPlayerId = holder.PlayerId,
            PlacementPlayerIdentity = holder.Guid,
            PersistentOwnerPlayerId = persistentOwnerPlayerId,
            // A materialized item needs a concrete projection slot even when it does not
            // carry a persistent recall claim. Flags distinguish an ordinary placement
            // slot from a reserved/locked retrieval claim.
            InventoryClaimPlayerId = snapshot.InventoryClaimPlayerId,
            InventoryClaimSlot = snapshot.InventoryClaimSlot,
            InventoryClaimFlags = snapshot.InventoryClaimFlags,
            PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
            Position = snapshot.ItemPosition,
            Rotation = snapshot.ItemRotation,
            LastReason = ItemTransitionReason.HostLocalState
        };
        records[item.NetId] = record;
        if (persistentOwnerPlayerId != 0 &&
            NetworkLifecycle.Instance.Server.TryGetServerPlayer(persistentOwnerPlayerId,
                out ServerPlayer owner))
        {
            record.PersistentOwnerIdentity = owner.Guid;
            owner.AddOwnedItem(item.NetId);
        }
        WriteToSnapshot(record, snapshot, record.LastReason);
        item.ApplyAuthorityMetadata(snapshot);
        Publish("item.cold-materialization-registered", item, record, new()
        {
            ["holderPlayerId"] = holder.PlayerId,
            ["persistentOwnerPlayerId"] = persistentOwnerPlayerId
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
        snapshot.WorldParentKind = record.WorldParentKind;
        snapshot.WorldParentNetId = record.WorldParentNetId;
        snapshot.WorldParentKey = record.WorldParentKey ?? string.Empty;
        snapshot.ParentLocalPosition = record.ParentLocalPosition;
        snapshot.ParentLocalRotation = record.ParentLocalRotation;
        snapshot.CarNetId = record.AttachedCarNetId;
        snapshot.AttachedFront = record.AttachedFront;
        snapshot.ItemPosition = record.Position;
        snapshot.ItemRotation = record.Rotation;
    }

    private static void WriteStateToSnapshot(ItemAuthorityState state, ItemUpdateData snapshot,
        ItemTransitionReason reason)
    {
        snapshot.AuthorityRevision = state.Revision;
        snapshot.PersistentOwnerPlayerId = state.PersistentOwnerPlayerId;
        snapshot.InventoryClaimPlayerId = state.InventoryClaimPlayerId;
        snapshot.InventoryClaimSlot = state.InventoryClaimSlot;
        snapshot.InventoryClaimFlags = (ItemInventoryClaimFlags)(byte)state.InventoryClaimFlags;
        snapshot.TransitionReason = reason;
        snapshot.PlayerId = state.PlacementPlayerId;
    }

    /// <summary>
    /// Builds a host-authored correction for a rejected speculative client transition.
    /// The registry supplies identity and placement; the Unity item supplies any state-specific
    /// attachment/transform fields needed by the normal snapshot application path.
    /// </summary>
    public static bool TryCreateCorrectionSnapshot(NetworkedItem item, out ItemUpdateData snapshot)
    {
        snapshot = null;
        if (item == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out Record record))
            return false;

        snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
        if (snapshot == null)
            return false;

        snapshot.ItemState = record.Placement switch
        {
            ItemPlacementKind.PlayerHand => ItemState.InHand,
            ItemPlacementKind.PlayerInventory => ItemState.InInventory,
            ItemPlacementKind.Attached => ItemState.Attached,
            _ => ItemState.Dropped
        };
        snapshot.ItemPosition = record.Position;
        snapshot.ItemRotation = record.Rotation;
        snapshot.OriginatingPlayerId = 0;
        WriteToSnapshot(record, snapshot, record.LastReason);
        return true;
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
            ["placementPlayerIdentity"] = record.PlacementPlayerIdentity == Guid.Empty ? string.Empty : record.PlacementPlayerIdentity.ToString("D"),
            ["persistentOwnerPlayerId"] = record.PersistentOwnerPlayerId,
            ["persistentOwnerIdentity"] = record.PersistentOwnerIdentity == Guid.Empty ? string.Empty : record.PersistentOwnerIdentity.ToString("D"),
            ["inventoryClaimPlayerId"] = record.InventoryClaimPlayerId,
            ["inventoryClaimSlot"] = record.InventoryClaimSlot,
            ["inventoryClaimFlags"] = record.InventoryClaimFlags.ToString(),
            ["prefabName"] = record.PrefabName,
            ["lastReason"] = record.LastReason.ToString(),
            ["persistentItemId"] = record.PersistentItemId == Guid.Empty ? string.Empty : record.PersistentItemId.ToString("D"),
            ["authoredItemKey"] = record.AuthoredItemKey ?? string.Empty,
            ["worldParentKind"] = record.WorldParentKind.ToString(),
            ["worldParentNetId"] = record.WorldParentNetId,
            ["worldParentKey"] = record.WorldParentKey ?? string.Empty
            ,["worldParentPersistentId"] = record.WorldParentPersistentId ?? string.Empty
            ,["hasPersistentOverride"] = record.HasPersistentOverride
            ,["attachedCarNetId"] = record.AttachedCarNetId
            ,["attachedCarPersistentId"] = record.AttachedCarPersistentId ?? string.Empty
            ,["attachedFront"] = record.AttachedFront
        };
    }

    public static void RebindPersistentOwner(ServerPlayer player)
    {
        if (player == null || player.Guid == Guid.Empty) return;
        foreach (Record record in records.Values)
        {
            if (record.PersistentOwnerIdentity != player.Guid) continue;
            record.PersistentOwnerPlayerId = player.PlayerId;
            if (record.InventoryClaimSlot >= 0)
                record.InventoryClaimPlayerId = player.PlayerId;
            player.AddOwnedItem(record.NetId);
        }
        foreach (Record record in records.Values)
            if (record.PlacementPlayerIdentity == player.Guid)
                record.PlacementPlayerId = player.PlayerId;
    }

    public static void UnbindPersistentOwner(ServerPlayer player)
    {
        if (player == null || player.Guid == Guid.Empty) return;
        foreach (Record record in records.Values)
        {
            if (record.PersistentOwnerIdentity != player.Guid) continue;
            record.PersistentOwnerPlayerId = 0;
            if (record.InventoryClaimSlot >= 0)
                record.InventoryClaimPlayerId = 0;
        }
        foreach (Record record in records.Values)
            if (record.PlacementPlayerIdentity == player.Guid)
                record.PlacementPlayerId = 0;
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
