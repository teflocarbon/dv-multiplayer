using Multiplayer.Core.Items.Projection;

namespace Multiplayer.Core.Items.Physics;

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
/// Who may change an item's object state (a lantern's flame, a radio's channel): the player
/// holding or carrying it, and a player who is working it where it lies (turning a set-down
/// lantern's knob, lighting it with their lighter). A value changing isn't enough to tell: every
/// game's copy of a world item drifts on its own (a lantern's knob is a physics body; a thrown
/// lantern's flame flickers in every game it flies through), and when those copies all reported
/// it the host's refusals set them off again, several times a second. So a non-holder's change
/// counts only inside a window its own player's interaction opened, and says so on the wire.
/// </summary>
public static class ItemObjectStateAuthority
{
    /// <summary>How far from the item a player working it may be, in metres (VR reach, a lighter).</summary>
    public const float InteractionReach = 6f;

    /// <summary>Whether a client sends a change to its copy's object state with no placement change.</summary>
    public static bool ClientMaySend(WireItemState localState, bool pageOnly, bool localInteraction) =>
        pageOnly || localInteraction || localState is WireItemState.InHand or WireItemState.InInventory;

    /// <summary>
    /// Whether the host takes a non-holder's object state for an item in the world: marked as an
    /// interaction, from a player whose game has the item, within reach of it.
    /// </summary>
    public static bool HostAcceptsWorldInteraction(bool markedInteraction, bool senderHasItem,
        bool itemInWorld, float senderDistance) =>
        markedInteraction && senderHasItem && itemInWorld && senderDistance <= InteractionReach;

    /// <summary>
    /// Whether the host drops an update without answering. A non-holder's object state isn't a
    /// dispute about where the item is, so a correction (which carries the position) only moved
    /// the sender's copy. Placement and position still get one.
    /// </summary>
    public static bool HostIgnores(bool carriesItemState, bool carriesPosition, bool carriesObjectState,
        bool carriesLifecycle, bool senderPossesses, bool allowedInteraction) =>
        carriesObjectState && !carriesItemState && !carriesPosition && !carriesLifecycle &&
        !senderPossesses && !allowedInteraction;
}

/// <summary>
/// The time a local player is working an item they don't hold: open while they hold one of its
/// controls (a lantern's knob), and for <see cref="GraceSeconds"/> after they let go of it or use
/// it, while the knob settles or the flame takes.
/// </summary>
public struct ItemInteractionWindow
{
    public const float GraceSeconds = 1.5f;

    private int held;
    private float until;

    public void Begin() => held++;

    public void End(float now)
    {
        if (held > 0) held--;
        Touch(now);
    }

    public void Touch(float now) => until = System.Math.Max(until, now + GraceSeconds);

    public bool IsOpen(float now) => held > 0 || now < until;

    public void Reset()
    {
        held = 0;
        until = 0f;
    }
}
