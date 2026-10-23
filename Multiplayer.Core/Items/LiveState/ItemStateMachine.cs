using System;
using System.Collections.Generic;
using Multiplayer.Core.Items.Physics;

namespace Multiplayer.Core.Items.LiveState;

public enum LivePlacement : byte
{
    World,
    PlayerHand,
    PlayerInventory,
    Container,
    Attached,
    LostAndFound,
    Installed,
    Destroyed,
    TrainInterior,
    StaticParent,
    SnappedAttachment
}

[Flags]
public enum ClaimFlags : byte
{
    None = 0,
    Reserved = 1,
    Locked = 2,
    Dropped = 4,
    Stolen = 8
}

/// <summary>A detached host-authoritative item record with no Unity or transport dependencies.</summary>
public sealed class LiveItemState
{
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    public LivePlacement Placement { get; set; }
    public byte PlacementPlayerId { get; set; }
    public byte PersistentOwnerPlayerId { get; set; }
    public byte InventoryClaimPlayerId { get; set; }
    public int InventoryClaimSlot { get; set; } = -1;
    public ClaimFlags InventoryClaimFlags { get; set; }
    /// <summary>
    /// The revisions from <see cref="ObjectStateRunBase"/> up to <see cref="ObjectStateRunTop"/>
    /// changed only object state (a lantern's flame), never placement. Means something only
    /// while <see cref="ObjectStateRunTop"/> equals <see cref="Revision"/>; see
    /// <see cref="ItemPlacementRevision"/>.
    /// </summary>
    public uint ObjectStateRunBase { get; set; }
    public uint ObjectStateRunTop { get; set; }

    public LiveItemState Clone() => new()
    {
        NetId = NetId,
        Revision = Revision,
        Placement = Placement,
        PlacementPlayerId = PlacementPlayerId,
        PersistentOwnerPlayerId = PersistentOwnerPlayerId,
        InventoryClaimPlayerId = InventoryClaimPlayerId,
        InventoryClaimSlot = InventoryClaimSlot,
        InventoryClaimFlags = InventoryClaimFlags,
        ObjectStateRunBase = ObjectStateRunBase,
        ObjectStateRunTop = ObjectStateRunTop
    };
}

public sealed class ItemTransition
{
    /// <summary>
    /// The actor id of the server itself: a host with no player of its own (a dedicated host with
    /// no local player session) committing what its own world did to an item, such as physics
    /// settling it, its own throws and the items it makes. It moves items about the world, but
    /// never holds one and never becomes an owner.
    /// </summary>
    public const byte ServerActorId = 0;

    /// <summary>
    /// The owner number of an item whose owner isn't connected (owned-items.md, PR 2a). Player
    /// numbers are per session, so an offline owner has none; this one is reserved and never given to
    /// a player. Every check that reads 0 as "unowned" then sees the item as owned, and no player
    /// matches it, so nobody else becomes its owner or recalls it, and it is someone else's to a
    /// borrower.
    /// </summary>
    public const byte AbsentOwnerId = 255;

    public byte ActorPlayerId { get; set; }
    public uint ExpectedRevision { get; set; }
    public bool Force { get; set; }
    public bool AppliesPlacement { get; set; }
    public LivePlacement RequestedPlacement { get; set; }
    public bool ClearRetrievalClaim { get; set; }
    /// <summary>
    /// TEMPORARY COMPATIBILITY ONLY: the host has authenticated an unconverted DV producer's
    /// player-marked item. Unlike a retrieval claim, this establishes identity even for an
    /// ordinary non-reserved item. Remove this transition after all producers use explicit
    /// host-authoritative creation operations.
    /// </summary>
    public bool EstablishPersistentOwner { get; set; }
    public bool MayEstablishPersistentOwner { get; set; } = true;
    /// <summary>
    /// The server's gadget code committing what vanilla did to an installed gadget's item: the
    /// only transition that may move an item into or out of <see cref="LivePlacement.Installed"/>.
    /// </summary>
    public bool GadgetTransition { get; set; }
    public int InventoryClaimSlot { get; set; } = -1;
    public ClaimFlags InventoryClaimFlags { get; set; }
}

public sealed class RecallCommand
{
    public byte RequestingPlayerId { get; set; }
    public uint ExpectedRevision { get; set; }
    public int RequestedSlot { get; set; } = -1;
}

public readonly struct TransitionResult
{
    private TransitionResult(bool accepted, string rejectionReason, LiveItemState state)
    {
        Accepted = accepted;
        RejectionReason = rejectionReason ?? string.Empty;
        State = state;
    }

    public bool Accepted { get; }
    public string RejectionReason { get; }
    public LiveItemState State { get; }

    public static TransitionResult Accept(LiveItemState state) => new(true, string.Empty, state);
    public static TransitionResult Reject(LiveItemState original, string reason) =>
        new(false, reason, original?.Clone());
}

/// <summary>
/// Pure canonical item transition rules. Rejections never mutate the supplied state and every
/// accepted operation returns a new state whose revision is exactly one greater.
/// </summary>
public static class ItemStateMachine
{
    public static TransitionResult Apply(LiveItemState current, ItemTransition command)
    {
        if (current == null || command == null || current.NetId == 0)
            return TransitionResult.Reject(current, "invalid-transition-input");
        bool serverActor = command.ActorPlayerId == ItemTransition.ServerActorId;
        if (serverActor && (command.EstablishPersistentOwner ||
                            command.AppliesPlacement && IsPlayerPlacement(command.RequestedPlacement)))
            return TransitionResult.Reject(current, "server-cannot-possess");
        if (!command.Force && current.Placement == LivePlacement.LostAndFound)
            return TransitionResult.Reject(current, "item-in-lost-and-found");
        // An installed gadget's item changes only with its gadget, forced or not: Lost and Found,
        // a recall or a player's own observation of it would leave its projection on the car.
        if (!command.GadgetTransition && command.AppliesPlacement &&
            (current.Placement == LivePlacement.Installed ||
             command.RequestedPlacement == LivePlacement.Installed))
            return TransitionResult.Reject(current, current.Placement == LivePlacement.Installed
                ? "item-installed" : "installed-only-by-gadgets");
        // A transition made against an older revision still holds if only object state changed
        // since: the server's copy of a lantern flickering mustn't refuse its holder's drop, nor
        // the re-grab of an item whose flame changed as it fell.
        if (!command.Force && !ItemPlacementRevision.UnchangedSince(command.ExpectedRevision,
                current.Revision, current.ObjectStateRunBase, current.ObjectStateRunTop))
            return TransitionResult.Reject(current, "stale-authority-revision");
        if (current.Revision == uint.MaxValue)
            return TransitionResult.Reject(current, "authority-revision-exhausted");

        if (command.AppliesPlacement && !command.Force &&
            IsPlayerPlacement(current.Placement) && current.PlacementPlayerId != 0 &&
            current.PlacementPlayerId != command.ActorPlayerId)
            return TransitionResult.Reject(current, "sender-not-current-possessor");

        LiveItemState next = current.Clone();
        bool requestedRetrievalClaim = command.InventoryClaimSlot >= 0 &&
            HasRetrievalFlag(command.InventoryClaimFlags);
        // An unowned personal item (a purchase, for one) belongs to whoever first takes it.
        bool establishesOwner = !serverActor && (command.EstablishPersistentOwner ||
            command.MayEstablishPersistentOwner &&
            (requestedRetrievalClaim || command.AppliesPlacement && IsPlayerPlacement(command.RequestedPlacement)));
        if (command.AppliesPlacement && next.PersistentOwnerPlayerId == 0 && establishesOwner)
        {
            next.PersistentOwnerPlayerId = command.ActorPlayerId;
            if (requestedRetrievalClaim)
            {
                next.InventoryClaimPlayerId = command.ActorPlayerId;
                next.InventoryClaimSlot = command.InventoryClaimSlot;
                next.InventoryClaimFlags = command.InventoryClaimFlags;
            }
        }

        if (command.AppliesPlacement)
        {
            next.Placement = command.RequestedPlacement;
            next.PlacementPlayerId = IsPlayerPlacement(command.RequestedPlacement)
                ? command.ActorPlayerId
                : (byte)0;
        }

        if (command.AppliesPlacement && next.PersistentOwnerPlayerId != 0 &&
            command.ActorPlayerId == next.PersistentOwnerPlayerId)
        {
            if (requestedRetrievalClaim)
            {
                next.InventoryClaimPlayerId = command.ActorPlayerId;
                bool ownerControlsCurrentPlacement = command.AppliesPlacement &&
                    IsPlayerPlacement(current.Placement) &&
                    current.PlacementPlayerId == command.ActorPlayerId;
                if (next.InventoryClaimSlot < 0 || ownerControlsCurrentPlacement)
                    next.InventoryClaimSlot = command.InventoryClaimSlot;
                next.InventoryClaimFlags = command.InventoryClaimFlags;
            }

            if (command.RequestedPlacement == LivePlacement.PlayerInventory)
                next.InventoryClaimFlags &= ~(ClaimFlags.Dropped | ClaimFlags.Stolen);
            else if (next.InventoryClaimSlot >= 0)
            {
                next.InventoryClaimFlags |= ClaimFlags.Dropped;
                next.InventoryClaimFlags &= ~ClaimFlags.Stolen;
            }
        }
        else if (command.AppliesPlacement && next.PersistentOwnerPlayerId != 0 &&
                 command.ActorPlayerId != next.PersistentOwnerPlayerId && next.InventoryClaimSlot >= 0)
        {
            next.InventoryClaimFlags |= ClaimFlags.Dropped |
                ClaimFlags.Reserved | ClaimFlags.Stolen;
        }

        if (command.Force && command.ClearRetrievalClaim)
        {
            next.PersistentOwnerPlayerId = 0;
            next.InventoryClaimPlayerId = 0;
            next.InventoryClaimSlot = -1;
            next.InventoryClaimFlags = ClaimFlags.None;
        }

        (next.ObjectStateRunBase, next.ObjectStateRunTop) = ItemPlacementRevision.RunAfter(
            current.Revision, current.ObjectStateRunBase, current.ObjectStateRunTop,
            command.AppliesPlacement);
        next.Revision++;
        return TransitionResult.Accept(next);
    }

    public static TransitionResult Recall(LiveItemState current, RecallCommand command)
    {
        if (current == null || command == null || current.NetId == 0 || command.RequestingPlayerId == 0)
            return TransitionResult.Reject(current, "unknown-network-entity");
        if (current.PersistentOwnerPlayerId == 0 ||
            current.PersistentOwnerPlayerId != command.RequestingPlayerId)
            return TransitionResult.Reject(current, "requester-not-persistent-owner");
        if (current.InventoryClaimSlot < 0 || !HasRetrievalFlag(current.InventoryClaimFlags))
            return TransitionResult.Reject(current, "item-not-recallable");
        if (current.Placement == LivePlacement.Installed)
            return TransitionResult.Reject(current, "item-installed");
        if (command.ExpectedRevision != current.Revision)
            return TransitionResult.Reject(current, "stale-authority-revision");
        if (current.Revision == uint.MaxValue)
            return TransitionResult.Reject(current, "authority-revision-exhausted");

        LiveItemState next = current.Clone();
        next.Placement = LivePlacement.PlayerInventory;
        next.PlacementPlayerId = command.RequestingPlayerId;
        next.InventoryClaimPlayerId = command.RequestingPlayerId;
        // The authenticated owner invoked recall from a concrete local ghost slot. That
        // slot is fresher evidence than a potentially stale canonical claim captured before
        // Derail Valley rebuilt or moved its inventory layout.
        if (command.RequestedSlot >= 0)
            next.InventoryClaimSlot = command.RequestedSlot;
        next.InventoryClaimFlags |= ClaimFlags.Reserved;
        next.InventoryClaimFlags &= ~(ClaimFlags.Dropped | ClaimFlags.Stolen);
        next.ObjectStateRunBase = next.ObjectStateRunTop = 0;
        next.Revision++;
        return TransitionResult.Accept(next);
    }

    private static bool IsPlayerPlacement(LivePlacement placement) =>
        placement is LivePlacement.PlayerHand or LivePlacement.PlayerInventory;

    private static bool HasRetrievalFlag(ClaimFlags flags) =>
        (flags & (ClaimFlags.Reserved | ClaimFlags.Locked)) != 0;
}

/// <summary>Small pure identity store used to prove one canonical record per nonzero NetId.</summary>
public sealed class LiveItemStore
{
    private readonly Dictionary<ushort, LiveItemState> items = new();

    public int Count => items.Count;

    public bool TryRegister(LiveItemState state)
    {
        if (state == null || state.NetId == 0 || items.ContainsKey(state.NetId))
            return false;
        items.Add(state.NetId, state.Clone());
        return true;
    }

    public bool TryGet(ushort netId, out LiveItemState state)
    {
        if (items.TryGetValue(netId, out LiveItemState stored))
        {
            state = stored.Clone();
            return true;
        }
        state = null;
        return false;
    }
}
