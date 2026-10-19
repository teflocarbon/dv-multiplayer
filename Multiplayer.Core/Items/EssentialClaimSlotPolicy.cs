using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items;

/// <summary>One essential's retrieval claim (Reserved or Locked) on its owner's inventory slot.</summary>
public readonly struct EssentialClaim
{
    public EssentialClaim(ushort netId, int slot, bool inOwnerInventory, bool dropped)
    {
        NetId = netId;
        Slot = slot;
        InOwnerInventory = inOwnerInventory;
        Dropped = dropped;
    }

    public ushort NetId { get; }
    public int Slot { get; }
    /// <summary>The item itself sits in its owner's inventory, so Derail Valley holds it in this slot.</summary>
    public bool InOwnerInventory { get; }
    /// <summary>The claim is a silhouette (Dropped or Stolen): the item is somewhere else.</summary>
    public bool Dropped { get; }
}

public readonly struct ClaimSlotMove
{
    public ClaimSlotMove(ushort netId, int fromSlot, int toSlot)
    {
        NetId = netId;
        FromSlot = fromSlot;
        ToSlot = toSlot;
    }

    public ushort NetId { get; }
    public int FromSlot { get; }
    public int ToSlot { get; }
}

/// <summary>
/// One player's essentials must each claim a slot of their own. A Derail Valley slot holds one item,
/// so of two claims on the same slot only one can be shown: the other essential got no silhouette
/// and could never be recalled. The item state machine sees one item at a time and cannot notice
/// this; the host checks an owner's claims together after any change and moves all but one.
/// </summary>
public static class EssentialClaimSlotPolicy
{
    /// <summary>DV.InventorySystem.Inventory.Capacity: slots 0 to 35, hotbar included.</summary>
    public const int InventoryCapacity = 36;

    /// <summary>
    /// Plans the moves that leave one owner's claims on distinct slots. Of the claims sharing a slot,
    /// <paramref name="preferredNetId"/> keeps it (the item the owner just put there), then an item
    /// actually in that slot, then a claim that isn't a silhouette, then the lowest NetId. The others
    /// move to the highest slot no claim holds: DV fills the backpack from the front, so the back is
    /// where an unclaimed item (which the host can't see) is least likely to sit.
    /// </summary>
    public static IReadOnlyList<ClaimSlotMove> Resolve(IEnumerable<EssentialClaim> ownerClaims,
        ushort preferredNetId = 0, int capacity = InventoryCapacity)
    {
        EssentialClaim[] claims = (ownerClaims ?? Enumerable.Empty<EssentialClaim>())
            .Where(claim => claim.NetId != 0 && claim.Slot >= 0)
            .GroupBy(claim => claim.NetId)
            .Select(group => group.First())
            .ToArray();
        List<EssentialClaim> displaced = new();
        HashSet<int> taken = new();
        foreach (IGrouping<int, EssentialClaim> slot in claims.GroupBy(claim => claim.Slot))
        {
            EssentialClaim[] ranked = slot
                .OrderByDescending(claim => preferredNetId != 0 && claim.NetId == preferredNetId)
                .ThenByDescending(claim => claim.InOwnerInventory)
                .ThenBy(claim => claim.Dropped)
                .ThenBy(claim => claim.NetId)
                .ToArray();
            taken.Add(slot.Key);
            displaced.AddRange(ranked.Skip(1));
        }

        List<ClaimSlotMove> moves = new();
        foreach (EssentialClaim claim in displaced.OrderBy(claim => claim.NetId))
        {
            int free = capacity - 1;
            while (free >= 0 && taken.Contains(free))
                free--;
            // A full inventory of claims leaves nowhere to go; keep the collision rather than drop a claim.
            if (free < 0)
                break;
            taken.Add(free);
            moves.Add(new ClaimSlotMove(claim.NetId, claim.Slot, free));
        }
        return moves;
    }
}
