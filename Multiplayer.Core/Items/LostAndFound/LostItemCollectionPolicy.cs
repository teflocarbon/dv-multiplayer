using System;
using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.LostAndFound;

public enum LostItemDecisionKind : byte
{
    Ineligible,
    Protected,
    WaitingForGrace,
    Collect
}

public sealed class LostItemCandidate
{
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    /// <summary>The owner's identity, or empty for an unowned item.</summary>
    public Guid Owner { get; set; }
    /// <summary>The owner has a session: an offline owner is an identity with none.</summary>
    public bool OwnerConnected { get; set; }
    public LivePlacement Placement { get; set; }
    public bool IsGrabbed { get; set; }
    public bool IsSnapped { get; set; }
    public bool IsInMachine { get; set; }
    public bool IsLicensePaper { get; set; }
    public bool IsPersonalItem { get; set; }
    public bool OwnerPositionKnown { get; set; }
    public float OwnerDistanceSquared { get; set; }
    public bool NearestPlayerPositionKnown { get; set; }
    public float NearestPlayerDistanceSquared { get; set; }
    public float EligibleDurationSeconds { get; set; }
}

public readonly struct LostItemDecision
{
    public LostItemDecision(LostItemDecisionKind kind, string reason)
    {
        Kind = kind;
        Reason = reason ?? string.Empty;
    }

    public LostItemDecisionKind Kind { get; }
    public string Reason { get; }
    public bool ShouldCollect => Kind == LostItemDecisionKind.Collect;
}

/// <summary>Pure host collection policy. Unknown state always protects the item.</summary>
public static class LostItemCollectionPolicy
{
    public static LostItemDecision Evaluate(LostItemCandidate item, float ownerDistance,
        float nearbyPlayerProtectionDistance, float graceSeconds)
    {
        if (item == null || item.NetId == 0 || item.Owner == Guid.Empty)
            return Ineligible("unowned");
        if (!item.IsPersonalItem)
            return Ineligible("not-a-personal-item");
        if (item.IsLicensePaper)
            return Ineligible("license-paper-excluded");
        // An offline owner counts as far away (decided by the user 2026-10-21), and their items are
        // collected from train interiors, shelves, couplers and snap points too (an end-of-train lantern,
        // a hanger: detached first, OI-LF-04); an online owner's from the world only.
        bool ownerAway = !item.OwnerConnected;
        if (item.Placement != LivePlacement.World &&
            !(ownerAway && item.Placement is LivePlacement.TrainInterior or LivePlacement.StaticParent
                or LivePlacement.Attached or LivePlacement.SnappedAttachment))
            return Ineligible("not-in-world");
        if (item.IsGrabbed || item.IsSnapped && !ownerAway || item.IsInMachine)
            return Ineligible("actively-contained-or-interacted");
        if (!ownerAway && !item.OwnerPositionKnown)
            return Protected("owner-position-unknown");
        if (!ownerAway && item.OwnerDistanceSquared < ownerDistance * ownerDistance)
            return Protected("owner-nearby");
        if (!item.NearestPlayerPositionKnown)
            return Protected("player-positions-unknown");
        if (item.NearestPlayerDistanceSquared < nearbyPlayerProtectionDistance * nearbyPlayerProtectionDistance)
            return Protected("another-player-nearby");
        if (item.EligibleDurationSeconds < graceSeconds)
            return new LostItemDecision(LostItemDecisionKind.WaitingForGrace, "collection-grace-period");
        return new LostItemDecision(LostItemDecisionKind.Collect, ownerAway ? "owner-away" : "owner-distance-eligible");
    }

    private static LostItemDecision Ineligible(string reason) =>
        new(LostItemDecisionKind.Ineligible, reason);
    private static LostItemDecision Protected(string reason) =>
        new(LostItemDecisionKind.Protected, reason);
}
