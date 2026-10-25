using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Core.Items.Projection;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Physics;

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
        LiveItemState state = State(revision: 4, placement: LivePlacement.PlayerHand, holder: 2);
        state = Apply(state, actor: 2, expected: 4, LivePlacement.World);          // drop -> 5
        state = Apply(state, actor: 1, expected: 0, null, force: true);                 // flame -> 6

        TransitionResult regrab = ItemStateMachine.Apply(state,
            Command(actor: 2, expected: 5, LivePlacement.PlayerHand));

        Assert.That(regrab.Accepted, Is.True, regrab.RejectionReason);
        Assert.That(regrab.State.Revision, Is.EqualTo(7));
        Assert.That(regrab.State.Placement, Is.EqualTo(LivePlacement.PlayerHand));
        Assert.That(regrab.State.Holder, Is.EqualTo(P(2)));
    }

    [Test]
    public void StateMachine_AcceptsHoldersSwitchOffAfterTheServersBatteryTicks()
    {
        // Player 3 carries a flashlight; the server's copy's battery ticks twice (playtest 3).
        LiveItemState state = State(revision: 4, placement: LivePlacement.World);
        state = Apply(state, actor: 3, expected: 4, LivePlacement.PlayerInventory); // -> 5
        state = Apply(state, actor: 1, expected: 0, null, force: true);                  // -> 6
        state = Apply(state, actor: 1, expected: 0, null, force: true);                  // -> 7

        Assert.That(state.Placement, Is.EqualTo(LivePlacement.PlayerInventory));
        Assert.That(state.Holder, Is.EqualTo(P(3)), "object state doesn't move it to the server");

        TransitionResult switchOff = ItemStateMachine.Apply(state,
            Command(actor: 3, expected: 5, null));

        Assert.That(switchOff.Accepted, Is.True, switchOff.RejectionReason);
        Assert.That(switchOff.State.Holder, Is.EqualTo(P(3)));
    }

    [Test]
    public void StateMachine_StillRefusesATransitionMadeBeforeTheItemMoved()
    {
        LiveItemState state = State(revision: 4, placement: LivePlacement.World);
        state = Apply(state, actor: 3, expected: 4, LivePlacement.PlayerHand);     // player 3 takes it -> 5
        state = Apply(state, actor: 3, expected: 5, null);                              // and lights it -> 6

        TransitionResult late = ItemStateMachine.Apply(state,
            Command(actor: 2, expected: 4, LivePlacement.PlayerHand));

        Assert.That(late.Accepted, Is.False);
        Assert.That(late.RejectionReason, Is.EqualTo("stale-authority-revision"));
    }

    [Test]
    public void StateMachine_AcceptsTheSettlersRegrabMadeBeforeItsOwnSettleCommit()
    {
        // OI-REF-02: player 2 drops a lantern (-> 5), its flame changes twice on the server
        // (-> 6, 7), and player 2's settle commit lands it (-> 8) a moment before their regrab,
        // which was made at 7.
        LiveItemState state = State(revision: 4, placement: LivePlacement.PlayerHand, holder: 2);
        state = Apply(state, actor: 2, expected: 4, LivePlacement.World);
        state = Apply(state, actor: 1, expected: 0, null, force: true);
        state = Apply(state, actor: 1, expected: 0, null, force: true);
        state = Settle(state, settler: 2);

        for (uint seen = 5; seen <= 8; seen++)
        {
            TransitionResult regrab = ItemStateMachine.Apply(state, Command(actor: 2, expected: seen, LivePlacement.PlayerHand));
            Assert.That(regrab.Accepted, Is.True, $"made at {seen}: {regrab.RejectionReason}");
        }
        Assert.That(ItemStateMachine.Apply(state, Command(actor: 2, expected: 4, LivePlacement.PlayerHand)).RejectionReason,
            Is.EqualTo("stale-authority-revision"), "made before the drop, when it was still in the hand");
        Assert.That(ItemStateMachine.Apply(state, Command(actor: 3, expected: 7, LivePlacement.PlayerHand)).RejectionReason,
            Is.EqualTo("stale-authority-revision"), "another player didn't simulate where it came to rest");
    }

    [Test]
    public void StateMachine_AFlightsParentChangeKeepsAnyonesGrabFromBeforeIt()
    {
        // A3: player 2 throws a mug (-> 5); it crosses into a flatcar mid-flight and the server
        // commits the new parent (-> 6). Player 3's catch, made at 5, still holds.
        LiveItemState thrown = Apply(State(revision: 4, placement: LivePlacement.PlayerHand, holder: 2),
            actor: 2, expected: 4, LivePlacement.World);
        LiveItemState inCar = ParentChange(thrown);
        TransitionResult caught = ItemStateMachine.Apply(inCar, Command(actor: 3, expected: 5, LivePlacement.PlayerHand));
        Assert.That(caught.Accepted, Is.True, caught.RejectionReason);
        Assert.That(ItemStateMachine.Apply(inCar, Command(actor: 2, expected: 5, LivePlacement.PlayerHand)).Accepted,
            Is.True, "the thrower's too");
        Assert.That(ItemStateMachine.Apply(inCar, Command(actor: 3, expected: 4, LivePlacement.PlayerHand)).RejectionReason,
            Is.EqualTo("stale-authority-revision"), "made before the throw, when it was in a hand");

        // The thrower's settlement in the car (-> 7) keeps the window from before the parent change
        // for the settler, and only for them.
        LiveItemState settled = Settle(inCar, settler: 2);
        Assert.That(ItemStateMachine.Apply(settled, Command(actor: 2, expected: 5, LivePlacement.PlayerHand)).Accepted,
            Is.True, "the settler's own request from before the parent change");
        Assert.That(ItemStateMachine.Apply(settled, Command(actor: 3, expected: 5, LivePlacement.PlayerHand)).Accepted,
            Is.False, "player 3 didn't simulate where it came to rest");

        // A second parent change (out of the car again, -> 7) keeps the first's window.
        Assert.That(ItemStateMachine.Apply(ParentChange(inCar), Command(actor: 3, expected: 5, LivePlacement.PlayerHand)).Accepted,
            Is.True, "two parent changes in one flight");
    }

    [Test]
    public void StateMachine_SettlersWindowOutlivesObjectStateButNotAMove()
    {
        LiveItemState state = State(revision: 4, placement: LivePlacement.World);
        state = Settle(state, settler: 2);                                              // -> 5
        state = Settle(state, settler: 2);                                              // settles again -> 6
        state = Apply(state, actor: 1, expected: 0, null, force: true);                 // flame -> 7

        Assert.That(ItemStateMachine.Apply(state, Command(actor: 2, expected: 4, LivePlacement.PlayerHand)).Accepted,
            Is.True, "both settlements were player 2's own, and only object state changed after");

        LiveItemState byOther = Settle(state, settler: 3);                              // -> 8
        Assert.That(ItemStateMachine.Apply(byOther, Command(actor: 2, expected: 7, LivePlacement.PlayerHand)).Accepted,
            Is.False, "player 3's settlement moved it since");

        LiveItemState byServer = Settle(state, settler: 0);
        Assert.That(ItemStateMachine.Apply(byServer, Command(actor: 2, expected: 7, LivePlacement.PlayerHand)).Accepted,
            Is.False, "the server's own settling moved it since");

        LiveItemState taken = Apply(state, actor: 3, expected: 7, LivePlacement.PlayerHand); // -> 8
        Assert.That(ItemStateMachine.Apply(taken, Command(actor: 2, expected: 6, LivePlacement.PlayerHand)).RejectionReason,
            Is.EqualTo("stale-authority-revision"), "player 3 took it since");
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
        Assert.That(ItemObjectStateAuthority.ClientMaySend(local, pageOnly, localInteraction: false), Is.EqualTo(sends));
    }

    [Test]
    public void ObjectState_HostIgnoresANonHoldersObjectStateButAnswersPlacement()
    {
        Assert.That(ItemObjectStateAuthority.HostIgnores(carriesItemState: false,
            carriesObjectState: true, carriesLifecycle: false, senderPossesses: false, allowedInteraction: false), Is.True);
        Assert.That(ItemObjectStateAuthority.HostIgnores(false, true, false, senderPossesses: true, false), Is.False);
        Assert.That(ItemObjectStateAuthority.HostIgnores(false, true, false, false, allowedInteraction: true), Is.False);
        Assert.That(ItemObjectStateAuthority.HostIgnores(carriesItemState: true, true, false, false, false), Is.False);
        Assert.That(ItemObjectStateAuthority.HostIgnores(false, true, carriesLifecycle: true, false, false), Is.False);
    }

    [Test]
    public void ObjectState_ANonHolderWorkingTheItemSendsIt()
    {
        Assert.That(ItemObjectStateAuthority.ClientMaySend(WireItemState.Dropped, pageOnly: false,
            localInteraction: true), Is.True);
        Assert.That(ItemObjectStateAuthority.ClientMaySend(WireItemState.Attached, pageOnly: false,
            localInteraction: true), Is.True);
    }

    [Test]
    public void ObjectState_HostTakesAMarkedInteractionFromANearbyPlayerWhoHasTheItem()
    {
        Assert.That(ItemObjectStateAuthority.HostAcceptsWorldInteraction(true, true, true, 1.5f), Is.True);
        Assert.That(ItemObjectStateAuthority.HostAcceptsWorldInteraction(false, true, true, 1.5f), Is.False,
            "a drifting copy's change isn't marked");
        Assert.That(ItemObjectStateAuthority.HostAcceptsWorldInteraction(true, false, true, 1.5f), Is.False,
            "the sender never had the item");
        Assert.That(ItemObjectStateAuthority.HostAcceptsWorldInteraction(true, true, false, 1.5f), Is.False,
            "someone else holds it");
        Assert.That(ItemObjectStateAuthority.HostAcceptsWorldInteraction(true, true, true,
            ItemObjectStateAuthority.InteractionReach + 0.1f), Is.False, "out of reach");
    }

    [Test]
    public void InteractionWindow_OpenWhileHeldAndForTheGraceAfter()
    {
        ItemInteractionWindow window = default;
        Assert.That(window.IsOpen(10f), Is.False);

        window.Begin();
        Assert.That(window.IsOpen(100f), Is.True, "held");
        window.End(100f);
        Assert.That(window.IsOpen(100f + ItemInteractionWindow.GraceSeconds - 0.1f), Is.True);
        Assert.That(window.IsOpen(100f + ItemInteractionWindow.GraceSeconds + 0.1f), Is.False);

        window.Touch(200f);   // a use: a lighter's click
        Assert.That(window.IsOpen(200.5f), Is.True);
        window.End(201f);     // an unmatched release doesn't hold it open
        Assert.That(window.IsOpen(201f + ItemInteractionWindow.GraceSeconds + 0.1f), Is.False);

        window.Begin();
        window.Reset();
        Assert.That(window.IsOpen(0f), Is.False);
    }

    [Test]
    public void OwnObjectStateEcho_IsNotReapplied()
    {
        Assert.That(ItemSnapshotApplicationPolicy.IsOwnObjectStateEcho(false, 2, 2, objectStateOnly: true), Is.True);
        Assert.That(ItemSnapshotApplicationPolicy.IsOwnObjectStateEcho(false, 2, 3, objectStateOnly: true), Is.False,
            "another player's change applies");
        Assert.That(ItemSnapshotApplicationPolicy.IsOwnObjectStateEcho(false, 2, 0, objectStateOnly: true), Is.False,
            "the host's own change applies");
        Assert.That(ItemSnapshotApplicationPolicy.IsOwnObjectStateEcho(false, 2, 2, objectStateOnly: false), Is.False,
            "placement acknowledgements have their own rules");
        Assert.That(ItemSnapshotApplicationPolicy.IsOwnObjectStateEcho(true, 2, 2, objectStateOnly: true), Is.False);
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

    // Players are identities, numbered here for short; 0 is nobody.
    private static System.Guid P(int number) => number == 0 ? System.Guid.Empty : new System.Guid(number, 0, 0, new byte[8]);

    private static LiveItemState State(uint revision, LivePlacement placement, int holder = 0) => new()
    {
        NetId = 801,
        Revision = revision,
        Placement = placement,
        Holder = P(holder)
    };

    private static ItemTransition Command(int actor, uint expected, LivePlacement? placement,
        bool force = false) => new()
    {
        Actor = P(actor),
        ExpectedRevision = expected,
        Force = force,
        AppliesPlacement = placement.HasValue,
        RequestedPlacement = placement ?? LivePlacement.World,
        MayEstablishPersistentOwner = false
    };

    private static LiveItemState Apply(LiveItemState state, int actor, uint expected,
        LivePlacement? placement, bool force = false)
    {
        TransitionResult result = ItemStateMachine.Apply(state, Command(actor, expected, placement, force));
        Assert.That(result.Accepted, Is.True, result.RejectionReason);
        return result.State;
    }

    // A settle commit as LiveItems.TryCommitSpatialPose makes it, by the player simulating it.
    private static LiveItemState Settle(LiveItemState state, int settler) => Commit(state, P(settler));

    // A flight's change of parent, as ServerItemSpatial.CommitParentChangeIfDue commits it.
    private static LiveItemState ParentChange(LiveItemState state) => Commit(state, ItemPlacementRevision.Anyone);

    private static LiveItemState Commit(LiveItemState state, System.Guid settler)
    {
        LiveItemState next = state.Clone();
        (next.SettledBy, next.SettledBase, next.SettledTop) = ItemPlacementRevision.SettledAfter(settler,
            state.Revision, state.ObjectStateRunBase, state.ObjectStateRunTop,
            state.SettledBy, state.SettledBase, state.SettledTop);
        next.Revision++;
        next.ObjectStateRunBase = next.ObjectStateRunTop = 0;
        return next;
    }
}
