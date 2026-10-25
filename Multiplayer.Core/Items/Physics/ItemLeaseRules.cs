using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.Physics;

/// <summary>What a committed change does to an item's physics lease.</summary>
public enum LeaseAfterChange : byte
{
    /// <summary>The change didn't move the item (object state only): the lease carries on.</summary>
    Keeps,
    /// <summary>A player let go of it into the world: a new lease begins, superseding any old one.</summary>
    Begins,
    /// <summary>It went anywhere else (a hand, an inventory, Lost and Found, a container, a recall, a destroy): the lease ends.</summary>
    Ends
}

/// <summary>
/// When an item's physics lease holds (docs/design/items/physics.md, "Leases"). A lease simulates an
/// item loose in the world, a car or on a shelf, from the revision it was granted at. Every change
/// that moves the item anywhere else ends it explicitly, at that change; nothing waits for a stale
/// sample to notice (OI-PHY-02, -03).
/// </summary>
public static class ItemLeaseRules
{
    /// <summary>Whether an item in this placement can be simulated: loose in the world, a car or on a shelf.</summary>
    public static bool IsSimulatable(LivePlacement placement) =>
        placement is LivePlacement.World or LivePlacement.TrainInterior or LivePlacement.StaticParent;

    /// <summary>
    /// Whether a lease granted at <paramref name="leaseRevision"/> still holds: the item is loose and
    /// only its object state has changed since (<see cref="ItemPlacementRevision.UnchangedSince"/>).
    /// The save takes a lease's pose only while it holds (OI-PER-11).
    /// </summary>
    public static bool Holds(LivePlacement placement, uint leaseRevision, uint revision, uint runBase, uint runTop) =>
        IsSimulatable(placement) && ItemPlacementRevision.UnchangedSince(leaseRevision, revision, runBase, runTop);

    /// <summary>
    /// What a committed change does to the lease. <paramref name="appliesPlacement"/>: the change
    /// sets where the item is (not object state only). <paramref name="released"/>: a player let go
    /// of it (a drop or a throw from a hand or an inventory, or the server's own release on a host
    /// with no player).
    /// </summary>
    public static LeaseAfterChange AfterChange(bool appliesPlacement, LivePlacement placement, bool released) =>
        !appliesPlacement ? LeaseAfterChange.Keeps
        : released && IsSimulatable(placement) ? LeaseAfterChange.Begins
        : LeaseAfterChange.Ends;
}
