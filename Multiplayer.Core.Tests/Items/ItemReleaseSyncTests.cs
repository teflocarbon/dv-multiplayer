using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class ItemReleaseSyncTests
{
    [Test]
    public void PlacementRevision_ObjectStateRunKeepsOlderRevisionsCurrent()
    {
        // Dropped at 4 -> 5, then the server's copy's flame twice: 5 -> 6 -> 7.
        (uint runBase, uint runTop) = ItemPlacementRevision.RunAfter(4, 0, 0, appliesPlacement: true);
        (runBase, runTop) = ItemPlacementRevision.RunAfter(5, runBase, runTop, appliesPlacement: false);
        (runBase, runTop) = ItemPlacementRevision.RunAfter(6, runBase, runTop, appliesPlacement: false);

        Assert.That((runBase, runTop), Is.EqualTo((5u, 7u)));
        Assert.That(ItemPlacementRevision.UnchangedSince(5, 7, runBase, runTop), Is.True);
        Assert.That(ItemPlacementRevision.UnchangedSince(6, 7, runBase, runTop), Is.True);
        Assert.That(ItemPlacementRevision.UnchangedSince(7, 7, runBase, runTop), Is.True);
        Assert.That(ItemPlacementRevision.UnchangedSince(4, 7, runBase, runTop), Is.False,
            "the drop itself moved the item");
        Assert.That(ItemPlacementRevision.UnchangedSince(8, 7, runBase, runTop), Is.False,
            "a revision from the future is never current");
    }

    [Test]
    public void PlacementRevision_RunEndsWhenAnythingElseAdvancesTheRevision()
    {
        (uint runBase, uint runTop) = ItemPlacementRevision.RunAfter(5, 0, 0, appliesPlacement: false);
        Assert.That((runBase, runTop), Is.EqualTo((5u, 6u)));

        // A spatial commit or a recall advances the record past the run's tip.
        Assert.That(ItemPlacementRevision.UnchangedSince(5, 7, runBase, runTop), Is.False);
        Assert.That(ItemPlacementRevision.UnchangedSince(6, 7, runBase, runTop), Is.False);
        // A new object-state change after it starts a fresh run at the new tip.
        Assert.That(ItemPlacementRevision.RunAfter(7, runBase, runTop, appliesPlacement: false),
            Is.EqualTo((7u, 8u)));
    }

    [Test]
    public void PlacementRevision_EmptyRunMatchesOnlyTheCurrentRevision()
    {
        Assert.That(ItemPlacementRevision.UnchangedSince(3, 3, 0, 0), Is.True);
        Assert.That(ItemPlacementRevision.UnchangedSince(0, 1, 0, 0), Is.False);
        Assert.That(ItemPlacementRevision.UnchangedSince(2, 3, 0, 0), Is.False);
    }

    [Test]
    public void StateMachine_AcceptsPickupMadeBeforeTheServersObjectStateChange()
    {
        ItemAuthorityState state = State(revision: 4, placement: AuthorityPlacement.PlayerHand, holder: 2);
        state = Apply(state, actor: 2, expected: 4, AuthorityPlacement.World);          // drop -> 5
        state = Apply(state, actor: 1, expected: 0, null, force: true);                 // flame -> 6

        ItemAuthorityResult regrab = ItemAuthorityStateMachine.Apply(state,
            Command(actor: 2, expected: 5, AuthorityPlacement.PlayerHand));

        Assert.That(regrab.Accepted, Is.True, regrab.RejectionReason);
        Assert.That(regrab.State.Revision, Is.EqualTo(7));
        Assert.That(regrab.State.Placement, Is.EqualTo(AuthorityPlacement.PlayerHand));
        Assert.That(regrab.State.PlacementPlayerId, Is.EqualTo(2));
    }

    [Test]
    public void StateMachine_StillRefusesATransitionMadeBeforeTheItemMoved()
    {
        ItemAuthorityState state = State(revision: 4, placement: AuthorityPlacement.World);
        state = Apply(state, actor: 3, expected: 4, AuthorityPlacement.PlayerHand);     // player 3 takes it -> 5
        state = Apply(state, actor: 3, expected: 5, null);                              // and lights it -> 6

        ItemAuthorityResult late = ItemAuthorityStateMachine.Apply(state,
            Command(actor: 2, expected: 4, AuthorityPlacement.PlayerHand));

        Assert.That(late.Accepted, Is.False);
        Assert.That(late.RejectionReason, Is.EqualTo("stale-authority-revision"));
    }

    [Test]
    public void ReleaseGrace_HoldsBackAGentleReleaseOnly()
    {
        Assert.That(ItemReleaseGrace.ShouldDefer(WireItemState.InHand, WireItemState.Dropped,
            thrown: false, relativeSpeed: 0.4f, secondsSinceRelease: 0.05f), Is.True);
        Assert.That(ItemReleaseGrace.ShouldDefer(WireItemState.InHand, WireItemState.Dropped,
            thrown: false, relativeSpeed: 0.4f, secondsSinceRelease: ItemReleaseGrace.GraceSeconds), Is.False,
            "sent once the grace is over");
        Assert.That(ItemReleaseGrace.ShouldDefer(WireItemState.InHand, WireItemState.Dropped,
            thrown: false, relativeSpeed: 6f, secondsSinceRelease: 0f), Is.False, "a VR throw goes at once");
        Assert.That(ItemReleaseGrace.ShouldDefer(WireItemState.InHand, WireItemState.Thrown,
            thrown: true, relativeSpeed: 0f, secondsSinceRelease: 0f), Is.False, "a desktop throw goes at once");
        Assert.That(ItemReleaseGrace.ShouldDefer(WireItemState.InHand, WireItemState.InInventory,
            thrown: false, relativeSpeed: 0f, secondsSinceRelease: 0f), Is.False, "stowing isn't a release");
        Assert.That(ItemReleaseGrace.ShouldDefer(WireItemState.InInventory, WireItemState.Dropped,
            thrown: false, relativeSpeed: 0f, secondsSinceRelease: 0f), Is.False, "dropping from the inventory");
    }

    [TestCase(WireItemState.InHand, false, true)]
    [TestCase(WireItemState.InInventory, false, true)]
    [TestCase(WireItemState.Dropped, false, false)]
    [TestCase(WireItemState.Attached, false, false)]
    [TestCase(WireItemState.Dropped, true, true)]
    public void ObjectState_OnlyTheHolderSendsIt(WireItemState local, bool pageOnly, bool sends)
    {
        Assert.That(ItemObjectStateAuthority.ClientMaySend(local, pageOnly), Is.EqualTo(sends));
    }

    [Test]
    public void ObjectState_HostIgnoresANonHoldersObjectStateButAnswersPlacementAndPosition()
    {
        Assert.That(ItemObjectStateAuthority.HostIgnores(carriesItemState: false, carriesPosition: false,
            carriesObjectState: true, carriesLifecycle: false, senderPossesses: false, pageOnly: false), Is.True);
        Assert.That(ItemObjectStateAuthority.HostIgnores(false, false, true, false, senderPossesses: true, false), Is.False);
        Assert.That(ItemObjectStateAuthority.HostIgnores(false, false, true, false, false, pageOnly: true), Is.False);
        Assert.That(ItemObjectStateAuthority.HostIgnores(carriesItemState: true, false, true, false, false, false), Is.False);
        Assert.That(ItemObjectStateAuthority.HostIgnores(false, carriesPosition: true, true, false, false, false), Is.False);
        Assert.That(ItemObjectStateAuthority.HostIgnores(false, false, true, carriesLifecycle: true, false, false), Is.False);
    }

    [Test]
    public void OwnReleaseAcknowledgement_IsSupersededOnceTheItemIsBackInHand()
    {
        Assert.That(ItemSnapshotApplicationPolicy.IsSupersededLocalRelease(false, 2, 2,
            WireItemState.Dropped, locallyPossessed: true), Is.True);
        Assert.That(ItemSnapshotApplicationPolicy.IsSupersededLocalRelease(false, 2, 2,
            WireItemState.Thrown, locallyPossessed: true), Is.True);
        Assert.That(ItemSnapshotApplicationPolicy.IsSupersededLocalRelease(false, 2, 2,
            WireItemState.Dropped, locallyPossessed: false), Is.False, "still lying where it fell");
        Assert.That(ItemSnapshotApplicationPolicy.IsSupersededLocalRelease(false, 2, 0,
            WireItemState.Dropped, locallyPossessed: true), Is.False, "a host correction always applies");
        Assert.That(ItemSnapshotApplicationPolicy.IsSupersededLocalRelease(false, 2, 3,
            WireItemState.Dropped, locallyPossessed: true), Is.False);
        Assert.That(ItemSnapshotApplicationPolicy.IsSupersededLocalRelease(true, 2, 2,
            WireItemState.Dropped, locallyPossessed: true), Is.False);
    }

    private static ItemAuthorityState State(uint revision, AuthorityPlacement placement, byte holder = 0) => new()
    {
        NetId = 801,
        Revision = revision,
        Placement = placement,
        PlacementPlayerId = holder
    };

    private static ItemTransitionCommand Command(byte actor, uint expected, AuthorityPlacement? placement,
        bool force = false) => new()
    {
        ActorPlayerId = actor,
        ExpectedRevision = expected,
        Force = force,
        AppliesPlacement = placement.HasValue,
        RequestedPlacement = placement ?? AuthorityPlacement.World,
        MayEstablishPersistentOwner = false
    };

    private static ItemAuthorityState Apply(ItemAuthorityState state, byte actor, uint expected,
        AuthorityPlacement? placement, bool force = false)
    {
        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(state, Command(actor, expected, placement, force));
        Assert.That(result.Accepted, Is.True, result.RejectionReason);
        return result.State;
    }
}
