namespace Multiplayer.Core.Items;

/// <summary>
/// Which authority revisions changed an item's placement. Every accepted change advances the
/// revision, object state included, so a revision alone can't tell a transition made against an
/// older revision whether the item has moved since. The record keeps the run of revisions at its
/// tip that changed only object state; a transition or physics lease from inside that run still
/// describes where the item is.
/// </summary>
public static class ItemPlacementRevision
{
    /// <summary>Whether placement is the same at <paramref name="current"/> as at <paramref name="revision"/>.</summary>
    public static bool UnchangedSince(uint revision, uint current, uint runBase, uint runTop) =>
        revision == current ||
        runTop == current && runTop != 0 && runBase <= revision && revision < current;

    /// <summary>The object-state run after an accepted change at <paramref name="revisionBefore"/>.</summary>
    public static (uint Base, uint Top) RunAfter(uint revisionBefore, uint runBase, uint runTop,
        bool appliesPlacement)
    {
        if (appliesPlacement || revisionBefore == uint.MaxValue)
            return (0, 0);
        uint after = revisionBefore + 1;
        // Extend the run if it reaches the old tip; otherwise it starts at the old tip.
        return runTop == revisionBefore && runTop != 0 ? (runBase, after) : (revisionBefore, after);
    }
}

/// <summary>
/// A hand letting go of an item isn't always a drop: a VR player passing it to the other hand, or
/// grabbing it again at another spot, lets go for a moment first. Sent at once, that release
/// became a drop the host accepted, and the new hold arrived as a change to an item the player no
/// longer had. A gentle release is held back briefly and sent only if no hand has it by then.
/// </summary>
public static class ItemReleaseGrace
{
    public const float GraceSeconds = 0.3f;
    /// <summary>Faster than this, relative to the player's car, the release is a throw.</summary>
    public const float ThrowSpeed = 2f;

    public static bool ShouldDefer(WireItemState lastSent, WireItemState current, bool thrown,
        float relativeSpeed, float secondsSinceRelease) =>
        lastSent == WireItemState.InHand && current == WireItemState.Dropped && !thrown &&
        relativeSpeed < ThrowSpeed && secondsSinceRelease < GraceSeconds;
}

/// <summary>
/// Who may change an item's object state (a lantern's flame, a radio's channel). Only the
/// player holding or carrying it: everyone else's copy of a world item drifts on its own (a thrown
/// lantern's flame flickers in every game it flies through), and when those copies all reported
/// it the host's refusals set them off again, several times a second.
/// </summary>
public static class ItemObjectStateAuthority
{
    /// <summary>Whether a client sends a change to its copy's object state with no placement change.</summary>
    public static bool ClientMaySend(WireItemState localState, bool pageOnly) =>
        pageOnly || localState is WireItemState.InHand or WireItemState.InInventory;

    /// <summary>
    /// Whether the host drops an update without answering. A non-holder's object state isn't a
    /// dispute about where the item is, so a correction (which carries the position) only moved
    /// the sender's copy. Placement and position still get one.
    /// </summary>
    public static bool HostIgnores(bool carriesItemState, bool carriesPosition, bool carriesObjectState,
        bool carriesLifecycle, bool senderPossesses, bool pageOnly) =>
        carriesObjectState && !carriesItemState && !carriesPosition && !carriesLifecycle &&
        !senderPossesses && !pageOnly;
}
