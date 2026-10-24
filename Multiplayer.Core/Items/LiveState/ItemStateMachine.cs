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
    SnappedAttachment,
    /// <summary>In a live tool's magazine (ammo: a cassette, a paint can, a spool), by the tool and slot.</summary>
    Magazine
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
    /// <summary>The identity of the player whose hand or inventory it's in, or empty.</summary>
    public Guid Holder { get; set; }
    /// <summary>The owner's identity, or empty for an unowned item. An offline owner is still its owner.</summary>
    public Guid Owner { get; set; }
    /// <summary>An essential's claim slot in its owner's inventory, or -1. A claim is always its owner's.</summary>
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
    /// <summary>
    /// The player whose own settle commits were the last placement changes, from
    /// <see cref="SettledBase"/> to <see cref="SettledTop"/>: their changes made against those
    /// revisions still hold (<see cref="ItemPlacementRevision.UnchangedFor"/>).
    /// </summary>
    public Guid SettledBy { get; set; }
    public uint SettledBase { get; set; }
    public uint SettledTop { get; set; }

    public LiveItemState Clone() => new()
    {
        NetId = NetId,
        Revision = Revision,
        Placement = Placement,
        Holder = Holder,
        Owner = Owner,
        InventoryClaimSlot = InventoryClaimSlot,
        InventoryClaimFlags = InventoryClaimFlags,
        ObjectStateRunBase = ObjectStateRunBase,
        ObjectStateRunTop = ObjectStateRunTop,
        SettledBy = SettledBy,
        SettledBase = SettledBase,
        SettledTop = SettledTop
    };
}

public sealed class ItemTransition
{
    /// <summary>
    /// The identity of the player making the change, or empty for the server itself: a host with
    /// no player of its own (a dedicated host with no local player session) committing what its own
    /// world did to an item, such as physics settling it, its own throws and the items it makes.
    /// The server moves items about the world, but never holds one and never becomes an owner.
    /// </summary>
    public Guid Actor { get; set; }
    public uint ExpectedRevision { get; set; }
    public bool Force { get; set; }
    public bool AppliesPlacement { get; set; }
    public LivePlacement RequestedPlacement { get; set; }
    public bool ClearRetrievalClaim { get; set; }
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
    /// <summary>The identity of the player asking for it back.</summary>
    public Guid Requester { get; set; }
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
        bool serverActor = command.Actor == Guid.Empty;
        if (serverActor && command.AppliesPlacement && IsPlayerPlacement(command.RequestedPlacement))
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
        // the re-grab of an item whose flame changed as it fell. Nor does one made before the
        // actor's own settle commit: their re-grab as it came to rest.
        if (!command.Force && !ItemPlacementRevision.UnchangedFor(command.Actor, command.ExpectedRevision,
                current.Revision, current.ObjectStateRunBase, current.ObjectStateRunTop,
                current.SettledBy, current.SettledBase, current.SettledTop))
            return TransitionResult.Reject(current, "stale-authority-revision");
        if (current.Revision == uint.MaxValue)
            return TransitionResult.Reject(current, "authority-revision-exhausted");

        if (command.AppliesPlacement && !command.Force &&
            IsPlayerPlacement(current.Placement) && current.Holder != Guid.Empty &&
            current.Holder != command.Actor)
            return TransitionResult.Reject(current, "sender-not-current-possessor");

        LiveItemState next = current.Clone();
        bool requestedRetrievalClaim = command.InventoryClaimSlot >= 0 &&
            HasRetrievalFlag(command.InventoryClaimFlags);
        // An unowned personal item (a purchase, for one) belongs to whoever first takes it.
        bool establishesOwner = !serverActor && command.MayEstablishPersistentOwner &&
            (requestedRetrievalClaim || command.AppliesPlacement && IsPlayerPlacement(command.RequestedPlacement));
        if (command.AppliesPlacement && next.Owner == Guid.Empty && establishesOwner)
        {
            next.Owner = command.Actor;
            if (requestedRetrievalClaim)
            {
                next.InventoryClaimSlot = command.InventoryClaimSlot;
                next.InventoryClaimFlags = command.InventoryClaimFlags;
            }
        }

        if (command.AppliesPlacement)
        {
            next.Placement = command.RequestedPlacement;
            next.Holder = IsPlayerPlacement(command.RequestedPlacement)
                ? command.Actor
                : Guid.Empty;
        }

        if (command.AppliesPlacement && next.Owner != Guid.Empty && command.Actor == next.Owner)
        {
            if (requestedRetrievalClaim)
            {
                bool ownerControlsCurrentPlacement = command.AppliesPlacement &&
                    IsPlayerPlacement(current.Placement) &&
                    current.Holder == command.Actor;
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
        else if (command.AppliesPlacement && next.Owner != Guid.Empty &&
                 command.Actor != next.Owner && next.InventoryClaimSlot >= 0)
        {
            next.InventoryClaimFlags |= ClaimFlags.Dropped |
                ClaimFlags.Reserved | ClaimFlags.Stolen;
        }

        if (command.Force && command.ClearRetrievalClaim)
        {
            next.Owner = Guid.Empty;
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
        if (current == null || command == null || current.NetId == 0 || command.Requester == Guid.Empty)
            return TransitionResult.Reject(current, "unknown-network-entity");
        if (current.Owner == Guid.Empty || current.Owner != command.Requester)
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
        next.Holder = command.Requester;
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
