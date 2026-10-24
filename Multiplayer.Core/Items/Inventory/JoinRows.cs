using System;

namespace Multiplayer.Core.Items.Inventory;

/// <summary>What a joining player is sent of items kept as data only (docs/design/items/inventory.md, "Joining").</summary>
public static class JoinRows
{
    /// <summary>
    /// OI-ESS-17: an essential of the joiner's (it has a claim) that another player holds as data
    /// only has no object for interest to find, so the joiner is sent it, for its silhouette.
    /// </summary>
    public static bool SendsHeldEssential(Guid owner, Guid holder, int claimSlot, Guid joiner) =>
        joiner != Guid.Empty && owner == joiner && holder != Guid.Empty && holder != joiner && claimSlot >= 0;
}
