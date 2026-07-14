using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Items;

public enum AuthorityPlacement : byte
{
    World,
    PlayerHand,
    PlayerInventory,
    Container,
    Attached,
    LostAndFound,
    Installed,
    Destroyed
}

[Flags]
public enum AuthorityClaimFlags : byte
{
    None = 0,
    Reserved = 1,
    Locked = 2,
    Dropped = 4,
    Stolen = 8
}

/// <summary>A detached host-authoritative item record with no Unity or transport dependencies.</summary>
public sealed class ItemAuthorityState
{
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    public AuthorityPlacement Placement { get; set; }
    public byte PlacementPlayerId { get; set; }
    public byte PersistentOwnerPlayerId { get; set; }
    public byte InventoryClaimPlayerId { get; set; }
    public int InventoryClaimSlot { get; set; } = -1;
    public AuthorityClaimFlags InventoryClaimFlags { get; set; }

    public ItemAuthorityState Clone() => new()
    {
        NetId = NetId,
        Revision = Revision,
        Placement = Placement,
        PlacementPlayerId = PlacementPlayerId,
        PersistentOwnerPlayerId = PersistentOwnerPlayerId,
        InventoryClaimPlayerId = InventoryClaimPlayerId,
        InventoryClaimSlot = InventoryClaimSlot,
        InventoryClaimFlags = InventoryClaimFlags
    };
}

public sealed class ItemTransitionCommand
{
    public byte ActorPlayerId { get; set; }
    public uint ExpectedRevision { get; set; }
    public bool Force { get; set; }
    public bool AppliesPlacement { get; set; }
    public AuthorityPlacement RequestedPlacement { get; set; }
    public bool ClearRetrievalClaim { get; set; }
    public int InventoryClaimSlot { get; set; } = -1;
    public AuthorityClaimFlags InventoryClaimFlags { get; set; }
}

public sealed class ItemRecallCommand
{
    public byte RequestingPlayerId { get; set; }
    public uint ExpectedRevision { get; set; }
    public int RequestedSlot { get; set; } = -1;
}

public readonly struct ItemAuthorityResult
{
    private ItemAuthorityResult(bool accepted, string rejectionReason, ItemAuthorityState state)
    {
        Accepted = accepted;
        RejectionReason = rejectionReason ?? string.Empty;
        State = state;
    }

    public bool Accepted { get; }
    public string RejectionReason { get; }
    public ItemAuthorityState State { get; }

    public static ItemAuthorityResult Accept(ItemAuthorityState state) => new(true, string.Empty, state);
    public static ItemAuthorityResult Reject(ItemAuthorityState original, string reason) =>
        new(false, reason, original?.Clone());
}

/// <summary>
/// Pure canonical item transition rules. Rejections never mutate the supplied state and every
/// accepted operation returns a new state whose revision is exactly one greater.
/// </summary>
public static class ItemAuthorityStateMachine
{
    public static ItemAuthorityResult Apply(ItemAuthorityState current, ItemTransitionCommand command)
    {
        if (current == null || command == null || current.NetId == 0 || command.ActorPlayerId == 0)
            return ItemAuthorityResult.Reject(current, "invalid-transition-input");
        if (!command.Force && command.ExpectedRevision != current.Revision)
            return ItemAuthorityResult.Reject(current, "stale-authority-revision");
        if (current.Revision == uint.MaxValue)
            return ItemAuthorityResult.Reject(current, "authority-revision-exhausted");

        if (command.AppliesPlacement && !command.Force &&
            IsPlayerPlacement(current.Placement) && current.PlacementPlayerId != 0 &&
            current.PlacementPlayerId != command.ActorPlayerId)
            return ItemAuthorityResult.Reject(current, "sender-not-current-possessor");

        ItemAuthorityState next = current.Clone();
        bool requestedRetrievalClaim = command.InventoryClaimSlot >= 0 &&
            HasRetrievalFlag(command.InventoryClaimFlags);
        if (command.AppliesPlacement && next.PersistentOwnerPlayerId == 0 && requestedRetrievalClaim)
        {
            next.PersistentOwnerPlayerId = command.ActorPlayerId;
            next.InventoryClaimPlayerId = command.ActorPlayerId;
            next.InventoryClaimSlot = command.InventoryClaimSlot;
            next.InventoryClaimFlags = command.InventoryClaimFlags;
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
                if (next.InventoryClaimSlot < 0)
                    next.InventoryClaimSlot = command.InventoryClaimSlot;
                next.InventoryClaimFlags = command.InventoryClaimFlags;
            }

            if (command.RequestedPlacement == AuthorityPlacement.PlayerInventory)
                next.InventoryClaimFlags &= ~(AuthorityClaimFlags.Dropped | AuthorityClaimFlags.Stolen);
            else if (next.InventoryClaimSlot >= 0)
            {
                next.InventoryClaimFlags |= AuthorityClaimFlags.Dropped;
                next.InventoryClaimFlags &= ~AuthorityClaimFlags.Stolen;
            }
        }
        else if (command.AppliesPlacement && next.PersistentOwnerPlayerId != 0 &&
                 command.ActorPlayerId != next.PersistentOwnerPlayerId && next.InventoryClaimSlot >= 0)
        {
            next.InventoryClaimFlags |= AuthorityClaimFlags.Dropped |
                AuthorityClaimFlags.Reserved | AuthorityClaimFlags.Stolen;
        }

        if (command.Force && command.ClearRetrievalClaim)
        {
            next.PersistentOwnerPlayerId = 0;
            next.InventoryClaimPlayerId = 0;
            next.InventoryClaimSlot = -1;
            next.InventoryClaimFlags = AuthorityClaimFlags.None;
        }

        next.Revision++;
        return ItemAuthorityResult.Accept(next);
    }

    public static ItemAuthorityResult Recall(ItemAuthorityState current, ItemRecallCommand command)
    {
        if (current == null || command == null || current.NetId == 0 || command.RequestingPlayerId == 0)
            return ItemAuthorityResult.Reject(current, "unknown-network-entity");
        if (current.PersistentOwnerPlayerId == 0 ||
            current.PersistentOwnerPlayerId != command.RequestingPlayerId)
            return ItemAuthorityResult.Reject(current, "requester-not-persistent-owner");
        if (current.InventoryClaimSlot < 0 || !HasRetrievalFlag(current.InventoryClaimFlags))
            return ItemAuthorityResult.Reject(current, "item-not-recallable");
        if (command.ExpectedRevision != current.Revision)
            return ItemAuthorityResult.Reject(current, "stale-authority-revision");
        if (current.Revision == uint.MaxValue)
            return ItemAuthorityResult.Reject(current, "authority-revision-exhausted");

        ItemAuthorityState next = current.Clone();
        next.Placement = AuthorityPlacement.PlayerInventory;
        next.PlacementPlayerId = command.RequestingPlayerId;
        next.InventoryClaimPlayerId = command.RequestingPlayerId;
        if (next.InventoryClaimSlot < 0 && command.RequestedSlot >= 0)
            next.InventoryClaimSlot = command.RequestedSlot;
        next.InventoryClaimFlags |= AuthorityClaimFlags.Reserved;
        next.InventoryClaimFlags &= ~(AuthorityClaimFlags.Dropped | AuthorityClaimFlags.Stolen);
        next.Revision++;
        return ItemAuthorityResult.Accept(next);
    }

    private static bool IsPlayerPlacement(AuthorityPlacement placement) =>
        placement is AuthorityPlacement.PlayerHand or AuthorityPlacement.PlayerInventory;

    private static bool HasRetrievalFlag(AuthorityClaimFlags flags) =>
        (flags & (AuthorityClaimFlags.Reserved | AuthorityClaimFlags.Locked)) != 0;
}

/// <summary>Small pure identity store used to prove one canonical record per nonzero NetId.</summary>
public sealed class ItemAuthorityStore
{
    private readonly Dictionary<ushort, ItemAuthorityState> items = new();

    public int Count => items.Count;

    public bool TryRegister(ItemAuthorityState state)
    {
        if (state == null || state.NetId == 0 || items.ContainsKey(state.NetId))
            return false;
        items.Add(state.NetId, state.Clone());
        return true;
    }

    public bool TryGet(ushort netId, out ItemAuthorityState state)
    {
        if (items.TryGetValue(netId, out ItemAuthorityState stored))
        {
            state = stored.Clone();
            return true;
        }
        state = null;
        return false;
    }
}
