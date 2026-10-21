using DV.InventorySystem;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items;

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
        public PlayerHand PlacementHand;
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
        // Why its placement, holder or owner last changed (ItemChangeLog).
        public ChangeCause<ItemMoveReason> LastMove;
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
        // The revisions that changed only object state; see ItemPlacementRevision.
        public uint ObjectStateRunBase;
        public uint ObjectStateRunTop;
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
        // An unclaimed personal item belongs to the actor only if they hold it. Registration often
        // happens when the host first replicates an item, which made the host the owner of every
        // item bought at a shop (purchases run on the host), whoever bought it.
        bool actorHoldsSeed = seed != null && actor != null && seed.PlayerId == actor.PlayerId &&
            seed.ItemState is ItemState.InHand or ItemState.InInventory;
        bool actorOwnsPlayerItem = item.Item?.InventorySpecs?.BelongsToPlayer == true && actor != null &&
            (claimedOwner == actor.PlayerId || claimedOwner == 0 && actorHoldsSeed);
        byte persistentOwner = actorOwnsPlayerItem ? actor.PlayerId : (byte)0;

        PersistentWorldItemIdentity persistentIdentity = item.GetComponent<PersistentWorldItemIdentity>();
        Guid persistentItemId = item.IsSceneAuthored ? Guid.Empty : OwnedItemsServer.ClaimPersistentId(item,
            (persistentIdentity ?? item.gameObject.AddComponent<PersistentWorldItemIdentity>()).Ensure());
        ItemPlacementKind initialPlacement = PlacementFrom(seed?.ItemState ?? item.DebugCurrentState);
        Vector3 initialPosition = seed?.ItemPosition ?? item.transform.position - WorldMover.currentMove;
        Quaternion initialRotation = seed?.ItemRotation ?? item.transform.rotation;
        Record record = new()
        {
            NetId = item.NetId,
            Placement = initialPlacement,
            PlacementPlayerId = seed?.PlayerId ?? 0,
            PlacementHand = initialPlacement == ItemPlacementKind.PlayerHand
                ? PlayerHandRules.Normalize(seed?.PlayerHand ?? PlayerHand.None)
                : PlayerHand.None,
            // Without a seed (an item the host just created or restored) this used to read
            // seed.PlayerId and throw, so the caller's registration never finished.
            PlacementPlayerIdentity = seed != null && seed.PlayerId != 0 && actor != null && actor.PlayerId == seed.PlayerId
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

    /// <param name="cause">
    /// Why, when the caller knows (a disconnect, a restore). Without it a change of placement,
    /// holder or owner is named from what changed (<see cref="ItemMoveClassifier"/>).
    /// </param>
    public static bool TryApplyTransition(NetworkedItem item, ItemUpdateData snapshot, ServerPlayer actor,
        ItemTransitionReason reason, bool force, out string rejectionReason,
        bool clearRetrievalClaim = false, ChangeCause<ItemMoveReason>? cause = null)
    {
        if (actor == null)
        {
            rejectionReason = "invalid-transition-input";
            return false;
        }
        return ApplyTransition(item, snapshot, actor, reason, force, out rejectionReason, clearRetrievalClaim, cause);
    }

    /// <summary>
    /// A change the server's own world made to an item, committed by the server itself rather than
    /// a player (<see cref="ItemTransitionCommand.ServerActorId"/>): on a host with no player of
    /// its own (<see cref="HostItemActor.IsServer"/>). It can move the item about the world but
    /// not into anyone's hands.
    /// </summary>
    public static bool TryApplyServerTransition(NetworkedItem item, ItemUpdateData snapshot,
        ItemTransitionReason reason, bool force, out string rejectionReason,
        ChangeCause<ItemMoveReason>? cause = null) =>
        ApplyTransition(item, snapshot, null, reason, force, out rejectionReason, false, cause);

    /// <summary>
    /// What vanilla did to an installed gadget's item, committed by the server's gadget code: the
    /// only transition into or out of <see cref="ItemPlacementKind.Installed"/> (Server/Gadgets).
    /// </summary>
    public static bool TryApplyGadgetTransition(NetworkedItem item, ItemUpdateData snapshot, ServerPlayer actor,
        ItemTransitionReason reason, out string rejectionReason, ChangeCause<ItemMoveReason>? cause = null) =>
        ApplyTransition(item, snapshot, actor, reason, true, out rejectionReason, false, cause, gadgetTransition: true);

    // actor null: the server itself.
    private static bool ApplyTransition(NetworkedItem item, ItemUpdateData snapshot, ServerPlayer actor,
        ItemTransitionReason reason, bool force, out string rejectionReason,
        bool clearRetrievalClaim, ChangeCause<ItemMoveReason>? cause, bool gadgetTransition = false)
    {
        rejectionReason = string.Empty;
        if (item == null || snapshot == null || item.NetId == 0)
        {
            rejectionReason = "invalid-transition-input";
            return false;
        }
        byte actorId = actor?.PlayerId ?? ItemTransitionCommand.ServerActorId;

        // A cause a client put in its own update is never passed on: only the host's goes out.
        snapshot.MoveCause = null;
        Record record = Ensure(item, actor, snapshot);
        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        bool retrievalEligible = item.Item?.InventorySpecs?.IsEssential == true;
        ItemPlacementKind previousPlacement = record.Placement;
        PlayerHand previousPlacementHand = record.PlacementHand;
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
            ["actorPlayerId"] = actorId,
            ["requestedState"] = snapshot.ItemState.ToString(),
            ["requestedRevision"] = snapshot.AuthorityRevision,
            ["force"] = force,
            ["reason"] = reason.ToString()
        });

        bool appliesPlacement = ItemUpdateData.ChangesPlacement(snapshot.UpdateType);
        ItemPlacementKind requestedPlacement = appliesPlacement ? PlacementFrom(snapshot.ItemState) : record.Placement;
        if (appliesPlacement && snapshot.ItemState is ItemState.Dropped or ItemState.Thrown)
            requestedPlacement = snapshot.WorldParentKind switch
            {
                ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
                ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
                _ => requestedPlacement
            };
        if (actor != null && requestedPlacement == ItemPlacementKind.PlayerInventory &&
            record.Placement != ItemPlacementKind.PlayerInventory &&
            !ItemAccessServer.Allows(actor, ItemAccessAction.Stash, item, record, out rejectionReason))
            return false;
        byte previousPersistentOwner = record.PersistentOwnerPlayerId;
        int previousClaimSlot = record.InventoryClaimSlot;
        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(currentState, new ItemTransitionCommand
        {
            ActorPlayerId = actorId,
            ExpectedRevision = snapshot.AuthorityRevision,
            Force = force,
            AppliesPlacement = appliesPlacement,
            RequestedPlacement = (AuthorityPlacement)(byte)requestedPlacement,
            ClearRetrievalClaim = clearRetrievalClaim,
            GadgetTransition = gadgetTransition,
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
            actor != null && record.PersistentOwnerPlayerId == actorId)
        {
            Publish("item.essential-claim-slot-move-rejected", item, record, new()
            {
                ["requestedSlot"] = snapshot.InventoryClaimSlot,
                ["retainedSlot"] = result.State.InventoryClaimSlot
            }, DebugSeverity.Warning);
        }

        ApplyCore(record, result.State);
        record.PlacementHand = appliesPlacement
            ? record.Placement == ItemPlacementKind.PlayerHand
                ? PlayerHandRules.Normalize(snapshot.PlayerHand)
                : PlayerHand.None
            : previousPlacementHand;
        // Object state alone (the server's copy of a flashlight a player carries) keeps the holder.
        if (appliesPlacement)
            record.PlacementPlayerIdentity = actor != null && record.PlacementPlayerId == actorId
                ? actor.Guid : Guid.Empty;
        KeepRemotePossession(item, record, snapshot);
        // The server is never an owner (the state machine makes sure), so the rest is a player's.
        if (actor != null && record.PersistentOwnerPlayerId == actorId && record.PersistentOwnerIdentity == Guid.Empty)
            record.PersistentOwnerIdentity = actor.Guid;
        if (actor != null && previousPersistentOwner == 0 && record.PersistentOwnerPlayerId == actorId)
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
        // The car an end-of-train lantern hangs on, or an installed gadget sits on.
        if ((record.Placement is ItemPlacementKind.Attached or ItemPlacementKind.Installed) &&
            NetworkedTrainCar.TryGet(record.AttachedCarNetId, out TrainCar attachedCar))
            record.AttachedCarPersistentId = attachedCar.CarGUID ?? string.Empty;
        if (item.IsSceneAuthored && snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState) &&
            snapshot.States is { Count: > 0 })
            record.HasPersistentOverride = true;
        record.LastReason = reason;
        WriteToSnapshot(record, snapshot, reason);
        ItemWhereabouts after = ItemChangeLog.Whereabouts(record);
        // A caller that names a cause moved it on purpose, if only to another spot in the world.
        if (cause.HasValue || ItemMoveClassifier.Changed(before, after))
        {
            ChangeCause<ItemMoveReason> move = TransitionCause(item, reason, actor, before, after,
                snapshot.ItemState == ItemState.Thrown, cause);
            snapshot.MoveCause = move;
            ItemChangeLog.RecordMove(item, record, move);
        }
        Publish("item.transition-accepted", item, record, new()
        {
            ["actorPlayerId"] = actorId,
            ["reason"] = reason.ToString()
        });
        ServerItemManager.Instance?.OnAuthoritativeItemTransition(item, snapshot, record, actor,
            previousPlacement, appliesPlacement);
        // The owner's client put the item in this slot, so it keeps the slot if another of their
        // essentials claims it too (one dropped or stolen leaves its slot looking empty in DV).
        bool ownerPlacedClaim = appliesPlacement && actor != null && record.PersistentOwnerPlayerId == actorId &&
            (record.InventoryClaimSlot != previousClaimSlot || record.Placement == ItemPlacementKind.PlayerInventory);
        if (HasRetrievalClaim(record))
            EnsureDistinctClaimSlots(record, ownerPlacedClaim ? record.NetId : (ushort)0, reason.ToString());
        return true;
    }

    private static ChangeCause<ItemMoveReason> TransitionCause(NetworkedItem item, ItemTransitionReason reason,
        ServerPlayer actor, ItemWhereabouts before, ItemWhereabouts after, bool thrown,
        ChangeCause<ItemMoveReason>? given)
    {
        string change = ItemChangeLog.Describe(before, after);
        if (given is { } known)
            return known.WithDetail(ItemChangeCauses.Detail(change, known.Detail));
        ItemMoveReason classified = reason switch
        {
            ItemTransitionReason.ClientAdoption => ItemMoveReason.Adopted,
            ItemTransitionReason.PersistenceRestore => ItemMoveReason.RestoredFromSave,
            ItemTransitionReason.JobBookletSummon => ItemMoveReason.JobBookletSummoned,
            _ => ItemMoveClassifier.Classify(before, after, thrown)
        };
        string source = reason switch
        {
            ItemTransitionReason.ClientState => null,
            // The host's own copy changed: on a dedicated host that is the game, not a player.
            ItemTransitionReason.HostLocalState => string.IsNullOrEmpty(item.ServerPart?.ObservationTrigger)
                ? "host's game" : "host's game, " + item.ServerPart?.ObservationTrigger,
            _ => reason.ToString()
        };
        return ItemChangeLog.Move(classified, actor, ItemChangeCauses.Detail(change, source,
            classified == ItemMoveReason.Unknown ? "callers " + ItemChangeCauses.Callers(typeof(AuthoritativeItemRegistry)) : null));
    }

    /// <summary>
    /// Puts the record's pose into an object-state update before it's applied, so a transition
    /// that doesn't move the item doesn't take the sender's copy's position as the item's.
    /// </summary>
    public static void KeepRecordedPose(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (item == null || snapshot == null || !records.TryGetValue(item.NetId, out Record record))
            return;
        snapshot.ItemPosition = record.Position;
        snapshot.ItemRotation = record.Rotation;
        snapshot.WorldParentKind = record.WorldParentKind;
        snapshot.WorldParentNetId = record.WorldParentNetId;
        snapshot.WorldParentKey = record.WorldParentKey ?? string.Empty;
        snapshot.ParentLocalPosition = record.ParentLocalPosition;
        snapshot.ParentLocalRotation = record.ParentLocalRotation;
        snapshot.CarNetId = record.AttachedCarNetId;
        snapshot.AttachedFront = record.AttachedFront;
    }

    /// <summary>
    /// Whether the item is where it was at <paramref name="revision"/>: only object state (a
    /// lantern's flame) has changed since. A physics lease granted then is still good.
    /// </summary>
    public static bool PlacementUnchangedSince(Record record, uint revision) =>
        record != null && ItemPlacementRevision.UnchangedSince(revision, record.Revision,
            record.ObjectStateRunBase, record.ObjectStateRunTop);

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
        if (!PlacementUnchangedSince(record, expectedRevision) || state.AuthorityRevision != expectedRevision)
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

        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        record.Revision++;
        record.ObjectStateRunBase = record.ObjectStateRunTop = 0;
        record.Placement = state.WorldParentKind switch
        {
            ItemWorldParentKind.TrainInterior => ItemPlacementKind.TrainInterior,
            ItemWorldParentKind.StaticParent => ItemPlacementKind.StaticParent,
            _ => ItemPlacementKind.World
        };
        record.PlacementPlayerId = 0;
        record.PlacementHand = PlayerHand.None;
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
        // Coming to rest where it was thrown isn't news, and would hide who threw it: only a
        // settlement that lands it somewhere else (into a car) is recorded.
        ItemWhereabouts after = ItemChangeLog.Whereabouts(record);
        if (ItemMoveClassifier.Changed(before, after))
            ItemChangeLog.RecordMove(item, record, ItemChangeLog.Move(ItemMoveReason.Settled,
                detail: ItemChangeLog.Describe(before, after)));
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
        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        ApplyCore(record, result.State);
        // The identity rebinds the placement when the player rejoins; left as the previous
        // holder's, a recalled item was handed back to that player's old session, and the
        // rejoining player got a second one.
        record.PlacementPlayerIdentity = record.PlacementPlayerId == requester.PlayerId
            ? requester.Guid : Guid.Empty;
        record.LastReason = ItemTransitionReason.OwnerRecall;
        snapshot = prepared;
        WriteToSnapshot(record, snapshot, ItemTransitionReason.OwnerRecall);
        ChangeCause<ItemMoveReason> recalled = ItemChangeLog.Move(ItemMoveReason.Recalled, requester,
            ItemChangeCauses.Detail(ItemChangeLog.Describe(before, ItemChangeLog.Whereabouts(record)),
                "slot " + record.InventoryClaimSlot));
        snapshot.MoveCause = recalled;
        ItemChangeLog.RecordMove(item, record, recalled);
        Publish("item.recall-accepted", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["previousPossessorPlayerId"] = previousPossessor,
            ["requestedSlot"] = requestedSlot,
            ["baseRevision"] = expectedRevision
        });
        EnsureDistinctClaimSlots(record, record.NetId, nameof(ItemTransitionReason.OwnerRecall));
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
        record.PlacementHand = PlayerHand.None;
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
        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        record.Revision++;
        record.Placement = ItemPlacementKind.PlayerInventory;
        record.PlacementPlayerId = requester.PlayerId;
        record.PlacementHand = PlayerHand.None;
        record.PlacementPlayerIdentity = requester.Guid;
        record.InventoryClaimPlayerId = requester.PlayerId;
        record.InventoryClaimSlot = targetSlot;
        record.InventoryClaimFlags &= ~(ItemInventoryClaimFlags.Dropped | ItemInventoryClaimFlags.Stolen);
        record.LastReason = ItemTransitionReason.LostAndFoundRetrieval;

        created.ItemState = ItemState.InInventory;
        created.PlayerId = requester.PlayerId;
        WriteToSnapshot(record, created, ItemTransitionReason.LostAndFoundRetrieval);
        ChangeCause<ItemMoveReason> retrieved = ItemChangeLog.Move(ItemMoveReason.RetrievedFromLostAndFound,
            requester, ItemChangeCauses.Detail(ItemChangeLog.Describe(before, ItemChangeLog.Whereabouts(record)),
                "slot " + targetSlot));
        created.MoveCause = retrieved;
        ItemChangeLog.RecordMove(item, record, retrieved);
        snapshot = created;
        Publish("item.lost-and-found-retrieval-authority-applied", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["targetSlot"] = targetSlot
        });
        EnsureDistinctClaimSlots(record, record.NetId, nameof(ItemTransitionReason.LostAndFoundRetrieval));
        return true;
    }

    public static Record RestoreLostRecord(NetworkedItem item, byte ownerPlayerId, uint revision,
        string prefabName, byte inventoryClaimPlayerId = 0, int inventoryClaimSlot = -1,
        ItemInventoryClaimFlags inventoryClaimFlags = ItemInventoryClaimFlags.None,
        ItemMoveReason reason = ItemMoveReason.HostCorrection)
    {
        if (item == null || item.NetId == 0 || ownerPlayerId == 0)
            return null;
        Record record = Ensure(item);
        record.Revision = revision;
        record.ObjectStateRunBase = record.ObjectStateRunTop = 0;
        record.Placement = ItemPlacementKind.LostAndFound;
        record.PlacementPlayerId = 0;
        record.PlacementHand = PlayerHand.None;
        record.PlacementPlayerIdentity = Guid.Empty;
        record.PersistentOwnerPlayerId = ownerPlayerId;
        record.InventoryClaimPlayerId = inventoryClaimPlayerId;
        record.InventoryClaimSlot = inventoryClaimSlot;
        record.InventoryClaimFlags = inventoryClaimFlags;
        record.PrefabName = string.IsNullOrWhiteSpace(prefabName) ? record.PrefabName : prefabName;
        record.LastReason = ItemTransitionReason.LostAndFoundCollection;
        ItemChangeLog.RecordMove(item, record, ItemChangeLog.Move(reason,
            detail: "in Lost and Found, owner " + (ItemChangeLog.PlayerName(ownerPlayerId) ?? "P" + ownerPlayerId) +
                    (reason == ItemMoveReason.HostCorrection ? ", retrieval rolled back" : null)));
        if (NetworkServer.Instance.TryGetServerPlayer(ownerPlayerId, out ServerPlayer owner))
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
        byte persistentOwnerPlayerId, ItemUpdateData snapshot, Guid persistentItemId)
    {
        if (item == null || holder == null || snapshot == null || item.NetId == 0)
            return null;
        // The item keeps its id through the container (owned-items.md, "Known bugs"): without it the
        // record wasn't saved.
        persistentItemId = OwnedItemsServer.ClaimPersistentId(item, persistentItemId);

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
            LastReason = ItemTransitionReason.HostLocalState,
            PersistentItemId = persistentItemId,
            AuthoredItemKey = item.AuthoredItemKey ?? string.Empty
        };
        records[item.NetId] = record;
        if (persistentOwnerPlayerId != 0 &&
            NetworkServer.Instance.TryGetServerPlayer(persistentOwnerPlayerId,
                out ServerPlayer owner))
        {
            record.PersistentOwnerIdentity = owner.Guid;
            owner.AddOwnedItem(item.NetId);
        }
        WriteToSnapshot(record, snapshot, record.LastReason);
        ChangeCause<ItemMoveReason> withdrawn = ItemChangeLog.Move(ItemMoveReason.WithdrawnFromContainer, holder,
            ItemChangeCauses.Detail(ItemChangeLog.Describe(
                    new ItemWhereabouts(AuthorityPlacement.Container, 0, persistentOwnerPlayerId),
                    ItemChangeLog.Whereabouts(record)),
                "slot " + record.InventoryClaimSlot));
        snapshot.MoveCause = withdrawn;
        ItemChangeLog.RecordMove(item, record, withdrawn);
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
        record.PlacementHand = PlayerHand.None;
        record.LastReason = previousReason;
        ItemChangeLog.RecordMove(item, record, ItemChangeLog.Move(ItemMoveReason.HostCorrection,
            detail: "Lost and Found collection rolled back to " + previousPlacement));
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
        snapshot.PlayerHand = record.Placement == ItemPlacementKind.PlayerHand
            ? record.PlacementHand : PlayerHand.None;
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
        snapshot.PlayerHand = PlayerHand.None;
    }

    /// <summary>
    /// Builds a host-authored correction for a rejected speculative client transition.
    /// The registry supplies identity and placement; the Unity item supplies any state-specific
    /// attachment/transform fields needed by the normal snapshot application path.
    /// </summary>
    /// <param name="cause">Why the recipient is being corrected, logged on their side (a refusal's reason).</param>
    public static bool TryCreateCorrectionSnapshot(NetworkedItem item, out ItemUpdateData snapshot,
        ChangeCause<ItemMoveReason>? cause = null)
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
            ItemPlacementKind.Installed => ItemState.Installed,
            _ => ItemState.Dropped
        };
        snapshot.ItemPosition = record.Position;
        snapshot.ItemRotation = record.Rotation;
        snapshot.OriginatingPlayerId = 0;
        WriteToSnapshot(record, snapshot, record.LastReason);
        snapshot.MoveCause = cause;
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
            ["placementHand"] = record.PlacementHand.ToString(),
            ["placementPlayerIdentity"] = record.PlacementPlayerIdentity == Guid.Empty ? string.Empty : record.PlacementPlayerIdentity.ToString("D"),
            ["persistentOwnerPlayerId"] = record.PersistentOwnerPlayerId,
            ["persistentOwnerIdentity"] = record.PersistentOwnerIdentity == Guid.Empty ? string.Empty : record.PersistentOwnerIdentity.ToString("D"),
            ["inventoryClaimPlayerId"] = record.InventoryClaimPlayerId,
            ["inventoryClaimSlot"] = record.InventoryClaimSlot,
            ["inventoryClaimFlags"] = record.InventoryClaimFlags.ToString(),
            ["prefabName"] = record.PrefabName,
            ["lastReason"] = record.LastReason.ToString(),
            ["lastMove"] = record.LastMove.Reason == ItemMoveReason.Unknown && record.LastMove.Detail.Length == 0
                ? string.Empty : record.LastMove.ToString(),
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

    /// <summary>Whether any record is owned by, or placed with, this player ID.</summary>
    internal static bool HasIdentity(Guid identity) =>
        identity != Guid.Empty && records.Values.Any(record =>
            record.PersistentOwnerIdentity == identity || record.PlacementPlayerIdentity == identity);

    /// <summary>A player's ID changed (InstallGuidRekey): their records follow. Returns how many changed.</summary>
    internal static int RekeyIdentity(Guid from, Guid to)
    {
        if (from == Guid.Empty || to == Guid.Empty)
            return 0;
        int changed = 0;
        foreach (Record record in records.Values)
        {
            bool touched = false;
            if (record.PersistentOwnerIdentity == from)
            {
                record.PersistentOwnerIdentity = to;
                touched = true;
            }
            if (record.PlacementPlayerIdentity == from)
            {
                record.PlacementPlayerIdentity = to;
                touched = true;
            }
            if (touched)
                changed++;
        }
        return changed;
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

    /// <summary>
    /// Moves the player's essentials apart if any two claim the same slot, as a save or an earlier
    /// session may have left them (EssentialClaimSlotPolicy). Returns how many claims moved.
    /// </summary>
    public static int EnsureDistinctClaimSlots(ServerPlayer owner, string reason)
    {
        if (owner == null || owner.Guid == Guid.Empty && owner.PlayerId == 0)
            return 0;
        return EnsureDistinctClaimSlots(records.Values.Where(record => HasRetrievalClaim(record) &&
            (owner.Guid != Guid.Empty && record.PersistentOwnerIdentity == owner.Guid ||
             owner.PlayerId != 0 && record.PersistentOwnerPlayerId == owner.PlayerId)).ToArray(), 0, reason);
    }

    // Checks the claims of this record's owner. preferredNetId keeps its slot; 0 lets the policy rank them.
    private static int EnsureDistinctClaimSlots(Record claimed, ushort preferredNetId, string reason)
    {
        if (claimed == null || claimed.PersistentOwnerIdentity == Guid.Empty && claimed.PersistentOwnerPlayerId == 0)
            return 0;
        return EnsureDistinctClaimSlots(records.Values.Where(record => HasRetrievalClaim(record) &&
            (claimed.PersistentOwnerIdentity != Guid.Empty && record.PersistentOwnerIdentity != Guid.Empty
                ? record.PersistentOwnerIdentity == claimed.PersistentOwnerIdentity
                : claimed.PersistentOwnerPlayerId != 0 &&
                  record.PersistentOwnerPlayerId == claimed.PersistentOwnerPlayerId)).ToArray(),
            preferredNetId, reason);
    }

    private static int EnsureDistinctClaimSlots(Record[] ownerClaims, ushort preferredNetId, string reason)
    {
        if (ownerClaims.Length < 2)
            return 0;
        IReadOnlyList<ClaimSlotMove> moves = EssentialClaimSlotPolicy.Resolve(ownerClaims.Select(record =>
            new EssentialClaim(record.NetId, record.InventoryClaimSlot, InOwnerInventory(record),
                (record.InventoryClaimFlags & (ItemInventoryClaimFlags.Dropped | ItemInventoryClaimFlags.Stolen)) != 0)),
            preferredNetId);
        foreach (ClaimSlotMove move in moves)
        {
            if (!records.TryGetValue(move.NetId, out Record record))
                continue;
            // The revision stays: only the owner's metadata changes, and a holder's next transition
            // must not be refused as stale for it.
            record.InventoryClaimSlot = move.ToSlot;
            NetworkedItem.TryGet(move.NetId, out NetworkedItem item);
            Publish("item.essential-claim-slot-collision-repaired", item, record, new()
            {
                ["fromSlot"] = move.FromSlot,
                ["toSlot"] = move.ToSlot,
                ["keptBy"] = ownerClaims.FirstOrDefault(other => other.NetId != move.NetId &&
                    other.InventoryClaimSlot == move.FromSlot)?.NetId ?? 0,
                ["reason"] = reason ?? string.Empty
            }, DebugSeverity.Warning);
            ushort keptBy = ownerClaims.FirstOrDefault(other => other.NetId != move.NetId &&
                other.InventoryClaimSlot == move.FromSlot)?.NetId ?? 0;
            ItemChangeLog.RecordMove(item, record, ItemChangeLog.Move(ItemMoveReason.ClaimSlotMoved,
                detail: $"claim slot {move.FromSlot} -> {move.ToSlot}, slot {move.FromSlot} kept by netId {keptBy}, after {reason}"));
            PushClaimSlotCorrection(item, record);
        }
        return moves.Count;
    }

    // Sends the moved claim to its owner, whose client then puts the silhouette in the new slot.
    private static void PushClaimSlotCorrection(NetworkedItem item, Record record)
    {
        bool lost = record.Placement == ItemPlacementKind.LostAndFound;
        if (lost)
            NetworkedLostAndFoundManager.SetInventoryClaimSlot(record.NetId, record.InventoryClaimSlot);
        if (item == null)
            return;
        ItemUpdateData correction = null;
        bool corrected = lost
            ? NetworkedLostAndFoundManager.TryCreateCollectionProjection(item, out correction)
            : TryCreateCorrectionSnapshot(item, out correction, record.LastMove);
        NetworkServer server = NetworkServer.Instance;
        bool hostOwns = server != null && record.PersistentOwnerPlayerId != 0 &&
            record.PersistentOwnerPlayerId == server.SelfId;
        if (corrected && hostOwns && !lost)
            item.ApplyServerCanonicalSnapshot(correction);
        else
            item.ApplyAuthorityMetadata(new ItemUpdateData
            {
                ItemNetId = item.NetId,
                AuthorityRevision = item.AuthorityRevision,
                PersistentOwnerPlayerId = record.PersistentOwnerPlayerId,
                InventoryClaimPlayerId = record.InventoryClaimPlayerId,
                InventoryClaimSlot = record.InventoryClaimSlot,
                InventoryClaimFlags = record.InventoryClaimFlags,
                TransitionReason = item.LastTransitionReason
            });
        // An owner still loading gets the claim with the rest of their items.
        if (corrected && server != null && !hostOwns &&
            server.TryGetServerPlayer(record.PersistentOwnerPlayerId, out ServerPlayer owner) &&
            owner.LoadingState >= PlayerLoadingState.ReadyForItems)
            server.SendItemUpdatePacket(correction, owner);
    }

    private static bool InOwnerInventory(Record record) =>
        record.Placement == ItemPlacementKind.PlayerInventory &&
        (record.PlacementPlayerIdentity != Guid.Empty && record.PlacementPlayerIdentity == record.PersistentOwnerIdentity ||
         record.PlacementPlayerId != 0 && record.PlacementPlayerId == record.PersistentOwnerPlayerId);

    private static bool HasRetrievalClaim(Record record) =>
        record.InventoryClaimSlot >= 0 && HasRetrievalFlag(record.InventoryClaimFlags);

    public static void Remove(ushort netId) => records.Remove(netId);
    public static void Clear()
    {
        records.Clear();
        ItemChangeLog.RecentRemovals.Clear();
    }
    public static IEnumerable<Record> Records => records.Values;

    /// <summary>
    /// A backstop for the server's own player taking another player's item. In playtest 2 items
    /// in a VR player's hand or inventory (lanterns as they were lit, an EOT lantern) ended up
    /// recorded in the server's player's hand, though the server's copy still belonged to the
    /// player; the next update moved them to the server's parked body and they vanished from the
    /// player's hand. Not every path that did it was found, so whatever the transition, a record
    /// that lands on the server's player while its item is another present player's goes back.
    /// </summary>
    private static void KeepRemotePossession(NetworkedItem item, Record record, ItemUpdateData snapshot)
    {
        if (record.Placement is not (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory))
            return;
        var server = NetworkServer.Instance;
        if (server == null || record.PlacementPlayerId != server.SelfId)
            return;
        byte possessor = item.playerBelongsToId;
        if (possessor == 0 || possessor == server.SelfId || !server.TryGetServerPlayer(possessor, out ServerPlayer holder))
            return;
        record.PlacementPlayerId = possessor;
        record.PlacementPlayerIdentity = holder.Guid;
        if (snapshot != null && snapshot.PlayerId == server.SelfId)
            snapshot.PlayerId = possessor;
        Multiplayer.LogWarning($"Item {item.NetId} ({record.PrefabName}) was recorded as the server's player's, " +
                               $"but it's {holder.Username}'s; kept it theirs");
    }

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
            InventoryClaimFlags = (AuthorityClaimFlags)(byte)record.InventoryClaimFlags,
            ObjectStateRunBase = record.ObjectStateRunBase,
            ObjectStateRunTop = record.ObjectStateRunTop
        };
    }

    private static void ApplyCore(Record record, ItemAuthorityState state)
    {
        record.Revision = state.Revision;
        record.Placement = (ItemPlacementKind)(byte)state.Placement;
        record.PlacementPlayerId = state.PlacementPlayerId;
        record.PlacementHand = PlayerHand.None;
        record.PersistentOwnerPlayerId = state.PersistentOwnerPlayerId;
        record.InventoryClaimPlayerId = state.InventoryClaimPlayerId;
        record.InventoryClaimSlot = state.InventoryClaimSlot;
        record.InventoryClaimFlags = (ItemInventoryClaimFlags)(byte)state.InventoryClaimFlags;
        record.ObjectStateRunBase = state.ObjectStateRunBase;
        record.ObjectStateRunTop = state.ObjectStateRunTop;
    }

    private static ItemPlacementKind PlacementFrom(ItemState state) => state switch
    {
        ItemState.InHand => ItemPlacementKind.PlayerHand,
        ItemState.InInventory => ItemPlacementKind.PlayerInventory,
        ItemState.Attached => ItemPlacementKind.Attached,
        ItemState.Installed => ItemPlacementKind.Installed,
        _ => ItemPlacementKind.World
    };

    private static bool HasRetrievalFlag(ItemInventoryClaimFlags flags) =>
        (flags & (ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Locked)) != 0;

    private static void PublishRejected(NetworkedItem item, Record record, ServerPlayer actor,
        ItemUpdateData snapshot, string reason)
    {
        Publish("item.transition-rejected", item, record, new()
        {
            ["actorPlayerId"] = actor?.PlayerId ?? ItemTransitionCommand.ServerActorId,
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
