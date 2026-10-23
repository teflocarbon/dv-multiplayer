using Multiplayer.Core.Items.LiveState;
using NUnit.Framework;
using System;

namespace Multiplayer.Core.Tests.Items.LiveState;

[TestFixture]
public sealed class ItemStateMachineTests
{
    [Test]
    public void Store_AllowsExactlyOneCanonicalItemPerNonzeroNetId()
    {
        LiveItemStore store = new();

        Assert.That(store.TryRegister(State(netId: 772)), Is.True);
        Assert.That(store.TryRegister(State(netId: 772)), Is.False);
        Assert.That(store.TryRegister(State(netId: 0)), Is.False);
        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(store.TryGet(772, out LiveItemState canonical), Is.True);
        Assert.That(canonical.NetId, Is.EqualTo(772));
    }

    [Test]
    public void AcceptedTransition_HasOnePlacementAndIncreasesRevisionExactlyOnce()
    {
        LiveItemState original = State(revision: 8, placement: LivePlacement.World);

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(actor: 2, revision: 8, placement: LivePlacement.PlayerHand));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.PlayerHand));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(2));
        Assert.That(result.State.Revision, Is.EqualTo(9));
        Assert.That(original.Revision, Is.EqualTo(8), "the input state must remain detached/immutable");
        Assert.That(original.Placement, Is.EqualTo(LivePlacement.World));
    }

    [Test]
    public void PickupByAnotherPlayer_NeverChangesPersistentOwner()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 12);

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(actor: 2, revision: 12, placement: LivePlacement.PlayerHand,
                retrievalClaim: true));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimPlayerId, Is.EqualTo(1));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Stolen), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Dropped), Is.True);
    }

    [Test]
    public void NonPersonalItemClaim_DoesNotEstablishPersistentOwnership()
    {
        LiveItemState original = State(revision: 2);
        ItemTransition command = Transition(actor: 1, revision: 2,
            placement: LivePlacement.PlayerInventory, retrievalClaim: true);
        command.MayEstablishPersistentOwner = false;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(-1));
    }

    [Test]
    public void PersonalItemPickup_EstablishesOwnerWithoutRetrievalClaim()
    {
        LiveItemState original = State(revision: 2);
        ItemTransition command = Transition(actor: 2, revision: 2,
            placement: LivePlacement.PlayerHand);
        command.MayEstablishPersistentOwner = true;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(-1));
    }

    [Test]
    public void PersonalItemMovedInWorld_DoesNotEstablishOwner()
    {
        LiveItemState original = State(revision: 2);
        ItemTransition command = Transition(actor: 2, revision: 2,
            placement: LivePlacement.World);
        command.MayEstablishPersistentOwner = true;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.Zero);
    }

    [Test]
    public void ClientAdoption_EstablishesOwnerWithoutRetrievalClaim()
    {
        LiveItemState original = State(revision: 0);
        ItemTransition command = Transition(actor: 2, revision: 0,
            placement: LivePlacement.PlayerInventory);
        command.EstablishPersistentOwner = true;
        command.MayEstablishPersistentOwner = false;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(2));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimPlayerId, Is.Zero,
            "ordinary ownership must not manufacture an essential-item recall claim");
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(-1));
        Assert.That(result.State.InventoryClaimFlags, Is.EqualTo(ClaimFlags.None));
    }

    [Test]
    public void LaterPickup_CannotReplaceOwnerEstablishedByClientAdoption()
    {
        LiveItemState original = State(revision: 1, placement: LivePlacement.World);
        original.PersistentOwnerPlayerId = 2;
        ItemTransition command = Transition(actor: 3, revision: 1,
            placement: LivePlacement.PlayerHand);
        command.EstablishPersistentOwner = true;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(2));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(3));
    }

    [Test]
    public void Recall_ReturnsSameCanonicalIdentityAndDoesNotCreateAnotherItem()
    {
        LiveItemStore store = new();
        LiveItemState borrowed = OwnedEssentialWorldItem(owner: 1, revision: 20);
        borrowed.Placement = LivePlacement.PlayerHand;
        borrowed.PlacementPlayerId = 2;
        borrowed.InventoryClaimFlags |= ClaimFlags.Stolen;
        Assert.That(store.TryRegister(borrowed), Is.True);

        TransitionResult result = ItemStateMachine.Recall(borrowed,
            Recall(player: 1, revision: 20, requestedSlot: 4));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.NetId, Is.EqualTo(borrowed.NetId));
        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(store.TryRegister(result.State), Is.False);
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.PlayerInventory));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(1));
        Assert.That(borrowed.Revision, Is.EqualTo(20),
            "recall preparation must not commit by mutating its canonical input");
        Assert.That(borrowed.Placement, Is.EqualTo(LivePlacement.PlayerHand));
        Assert.That(borrowed.PlacementPlayerId, Is.EqualTo(2));
    }

    [Test]
    public void Recall_RebasesClaimToClickedGhostAndClearsDroppedAndStolen()
    {
        LiveItemState borrowed = OwnedEssentialWorldItem(owner: 1, revision: 20);
        borrowed.InventoryClaimSlot = 3;
        borrowed.InventoryClaimFlags = ClaimFlags.Reserved |
            ClaimFlags.Dropped | ClaimFlags.Stolen;

        TransitionResult result = ItemStateMachine.Recall(borrowed,
            Recall(player: 1, revision: 20, requestedSlot: 9));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(9));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Reserved), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Dropped), Is.False);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Stolen), Is.False);
    }

    [Test]
    public void RecallByNonOwner_IsRejectedWithoutMutation()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 4);

        TransitionResult result = ItemStateMachine.Recall(original,
            Recall(player: 2, revision: 4, requestedSlot: 0));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("requester-not-persistent-owner"));
        Assert.That(result.State.Revision, Is.EqualTo(4));
        Assert.That(result.State.Placement, Is.EqualTo(original.Placement));
        Assert.That(original.Revision, Is.EqualTo(4));
    }

    [Test]
    public void StaleRevision_IsRejectedWithoutChangingCanonicalState()
    {
        LiveItemState original = State(revision: 7, placement: LivePlacement.World);

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(actor: 1, revision: 6, placement: LivePlacement.PlayerHand));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("stale-authority-revision"));
        Assert.That(result.State.Revision, Is.EqualTo(7));
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.World));
    }

    [Test]
    public void LostAndFoundPlacement_RejectsLiveClientResurrectionWithoutMutation()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 12);
        original.Placement = LivePlacement.LostAndFound;
        original.PlacementPlayerId = 0;

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(actor: 2, revision: 12, placement: LivePlacement.PlayerHand));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("item-in-lost-and-found"));
        Assert.That(result.State.Revision, Is.EqualTo(12));
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.LostAndFound));
        Assert.That(original.Placement, Is.EqualTo(LivePlacement.LostAndFound));
    }

    [Test]
    public void CurrentPossessor_BlocksAnotherPlayersMutation()
    {
        LiveItemState original = State(revision: 11, placement: LivePlacement.PlayerHand);
        original.PlacementPlayerId = 1;

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(actor: 2, revision: 11, placement: LivePlacement.World));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("sender-not-current-possessor"));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(1));
        Assert.That(result.State.Revision, Is.EqualTo(11));
    }

    [Test]
    public void RevisionsNeverWrapAround()
    {
        LiveItemState original = State(revision: uint.MaxValue,
            placement: LivePlacement.World);

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(actor: 1, revision: uint.MaxValue,
                placement: LivePlacement.PlayerHand));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("authority-revision-exhausted"));
        Assert.That(result.State.Revision, Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void FirstReservedInventoryTransition_EstablishesOwnerAndClaim()
    {
        LiveItemState original = State();
        ItemTransition command = Transition(2, 0, LivePlacement.PlayerInventory,
            retrievalClaim: true);
        command.InventoryClaimSlot = 5;
        command.InventoryClaimFlags = ClaimFlags.Reserved;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(5));
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.PlayerInventory));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Dropped), Is.False);
    }

    [Test]
    public void OwnerWorldTransition_RetainsReservedDroppedClaim()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 3);
        original.Placement = LivePlacement.PlayerHand;
        original.PlacementPlayerId = 1;

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(1, 3, LivePlacement.World, retrievalClaim: true));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PlacementPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(3));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Reserved), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Dropped), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Stolen), Is.False);
    }

    [Test]
    public void BorrowDrop_KeepsOwnerAndStolenClaimUntilRecall()
    {
        LiveItemState borrowed = OwnedEssentialWorldItem(owner: 1, revision: 10);
        borrowed.Placement = LivePlacement.PlayerHand;
        borrowed.PlacementPlayerId = 2;
        borrowed.InventoryClaimFlags |= ClaimFlags.Stolen;

        TransitionResult dropped = ItemStateMachine.Apply(borrowed,
            Transition(2, 10, LivePlacement.World, retrievalClaim: true));

        Assert.That(dropped.Accepted, Is.True);
        Assert.That(dropped.State.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(dropped.State.Placement, Is.EqualTo(LivePlacement.World));
        Assert.That(dropped.State.InventoryClaimFlags.HasFlag(ClaimFlags.Stolen), Is.True);
    }

    [Test]
    public void RetrievalClaimSlot_CannotBeMovedWhileItemIsOutsideOwnerPossession()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 6);
        ItemTransition command = Transition(1, 6, LivePlacement.PlayerInventory,
            retrievalClaim: true);
        command.InventoryClaimSlot = 9;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(3));
    }

    [Test]
    public void OwnerMayMoveRetrievalClaimWhileCurrentlyPossessingItem()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 6);
        original.Placement = LivePlacement.PlayerHand;
        original.PlacementPlayerId = 1;
        ItemTransition command = Transition(1, 6, LivePlacement.World,
            retrievalClaim: true);
        command.InventoryClaimSlot = 9;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(9));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Dropped), Is.True);
    }

    [Test]
    public void ForeignPossessorCannotMoveOwnersRetrievalClaim()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 6);
        original.Placement = LivePlacement.PlayerHand;
        original.PlacementPlayerId = 2;
        ItemTransition command = Transition(2, 6, LivePlacement.World,
            retrievalClaim: true);
        command.InventoryClaimSlot = 9;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(3));
    }

    [Test]
    public void OrdinaryInventorySlot_OwnsPersonalItemWithoutRetrievalClaim()
    {
        LiveItemState original = State(revision: 6, placement: LivePlacement.World);
        ItemTransition command = Transition(1, 6, LivePlacement.PlayerInventory);
        command.InventoryClaimSlot = 9;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(-1));
    }

    [Test]
    public void ObjectOnlyUpdate_IncreasesRevisionWithoutChangingPlacement()
    {
        LiveItemState original = State(revision: 2,
            placement: LivePlacement.PlayerHand);
        original.PlacementPlayerId = 1;
        ItemTransition command = Transition(1, 2, LivePlacement.World);
        command.AppliesPlacement = false;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Revision, Is.EqualTo(3));
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.PlayerHand));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(1));
    }

    [Test]
    public void ForcedHostProjection_BypassesPossessorAndRevisionButNotPersistentOwnership()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 8);
        original.Placement = LivePlacement.PlayerHand;
        original.PlacementPlayerId = 2;
        ItemTransition command = Transition(3, 1, LivePlacement.World,
            retrievalClaim: true);
        command.Force = true;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Revision, Is.EqualTo(9));
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.World));
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(1));
    }

    [Test]
    public void ForcedSharedSummon_ClearsPlayerRetrievalClaim()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 2, revision: 8);
        original.Placement = LivePlacement.PlayerInventory;
        original.PlacementPlayerId = 2;
        ItemTransition command = Transition(1, 0, LivePlacement.World);
        command.Force = true;
        command.ClearRetrievalClaim = true;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Placement, Is.EqualTo(LivePlacement.World));
        Assert.That(result.State.PersistentOwnerPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(-1));
        Assert.That(result.State.InventoryClaimFlags, Is.EqualTo(ClaimFlags.None));
    }

    [Test]
    public void Recall_RejectsStaleRevisionAndItemWithoutRetrievalClaim()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 8);
        TransitionResult stale = ItemStateMachine.Recall(original,
            Recall(1, 7, 3));
        LiveItemState noClaim = original.Clone();
        noClaim.InventoryClaimFlags = ClaimFlags.None;
        TransitionResult nonRecallable = ItemStateMachine.Recall(noClaim,
            Recall(1, 8, 3));

        Assert.That(stale.RejectionReason, Is.EqualTo("stale-authority-revision"));
        Assert.That(nonRecallable.RejectionReason, Is.EqualTo("item-not-recallable"));
        Assert.That(original.Revision, Is.EqualTo(8));
    }

    [Test]
    public void TenPickupDropCycles_PreserveIdentityOwnerClaimAndMonotonicRevision()
    {
        LiveItemState state = OwnedEssentialWorldItem(owner: 1, revision: 0);
        for (int cycle = 0; cycle < 10; cycle++)
        {
            TransitionResult pickup = ItemStateMachine.Apply(state,
                Transition(2, state.Revision, LivePlacement.PlayerHand, retrievalClaim: true));
            Assert.That(pickup.Accepted, Is.True, $"pickup cycle {cycle}");
            state = pickup.State;

            TransitionResult drop = ItemStateMachine.Apply(state,
                Transition(2, state.Revision, LivePlacement.World, retrievalClaim: true));
            Assert.That(drop.Accepted, Is.True, $"drop cycle {cycle}");
            state = drop.State;
        }

        Assert.That(state.NetId, Is.EqualTo(772));
        Assert.That(state.Revision, Is.EqualTo(20));
        Assert.That(state.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(state.InventoryClaimSlot, Is.EqualTo(3));
        Assert.That(state.Placement, Is.EqualTo(LivePlacement.World));
    }

    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.LostAndFound)]
    [TestCase(LivePlacement.Installed)]
    [TestCase(LivePlacement.Destroyed)]
    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.StaticParent)]
    [TestCase(LivePlacement.SnappedAttachment)]
    public void NonPlayerPlacements_AlwaysClearPlacementPlayer(LivePlacement placement)
    {
        LiveItemState original = State(revision: 1,
            placement: LivePlacement.PlayerHand);
        original.PlacementPlayerId = 2;
        ItemTransition command = Transition(2, 1, placement);
        // Installing from a hand is the gadget code's transition (InstalledItem_IsMovedOnlyByItsGadget).
        command.GadgetTransition = placement == LivePlacement.Installed;

        TransitionResult result = ItemStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PlacementPlayerId, Is.Zero);
    }

    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.StaticParent)]
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.Destroyed)]
    public void ServerActor_MovesAnItemAboutTheWorld(LivePlacement placement)
    {
        LiveItemState original = State(revision: 4, placement: LivePlacement.World);

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(ItemTransition.ServerActorId, 4, placement));

        Assert.That(result.Accepted, Is.True, result.RejectionReason);
        Assert.That(result.State.Placement, Is.EqualTo(placement));
        Assert.That(result.State.PlacementPlayerId, Is.Zero);
        Assert.That(result.State.Revision, Is.EqualTo(5));
    }

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    public void ServerActor_NeverHoldsAnItem(LivePlacement placement)
    {
        LiveItemState original = State(revision: 4, placement: LivePlacement.World);

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(ItemTransition.ServerActorId, 4, placement));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("server-cannot-possess"));
        Assert.That(result.State.Revision, Is.EqualTo(4));
    }

    [Test]
    public void ServerActor_NeverBecomesAnOwner()
    {
        LiveItemState original = State(revision: 4);
        ItemTransition adoption = Transition(ItemTransition.ServerActorId, 4,
            LivePlacement.World, retrievalClaim: true);
        adoption.EstablishPersistentOwner = true;

        Assert.That(ItemStateMachine.Apply(original, adoption).Accepted, Is.False);

        adoption.EstablishPersistentOwner = false;
        TransitionResult moved = ItemStateMachine.Apply(original, adoption);

        Assert.That(moved.Accepted, Is.True);
        Assert.That(moved.State.PersistentOwnerPlayerId, Is.Zero);
        Assert.That(moved.State.InventoryClaimPlayerId, Is.Zero);
        Assert.That(moved.State.InventoryClaimSlot, Is.EqualTo(-1));
    }

    [Test]
    public void ServerActor_MovingAnOwnersDroppedItem_KeepsTheOwnersClaimReserved()
    {
        LiveItemState original = OwnedEssentialWorldItem(owner: 1, revision: 6);

        TransitionResult result = ItemStateMachine.Apply(original,
            Transition(ItemTransition.ServerActorId, 6, LivePlacement.TrainInterior));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(result.State.InventoryClaimPlayerId, Is.EqualTo(1));
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(3));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(ClaimFlags.Reserved), Is.True);
    }

    [Test]
    public void ServerActor_CannotTakeAnItemOutOfAPlayersHandUnlessForced()
    {
        LiveItemState held = State(revision: 3, placement: LivePlacement.PlayerHand);
        held.PlacementPlayerId = 2;

        Assert.That(ItemStateMachine.Apply(held,
            Transition(ItemTransition.ServerActorId, 3, LivePlacement.World)).RejectionReason,
            Is.EqualTo("sender-not-current-possessor"));
    }

    [Test]
    public void DeterministicMixedTransitionFuzz_PreservesCoreInvariants()
    {
        Random random = new(7722026);
        LiveItemState state = OwnedEssentialWorldItem(owner: 1, revision: 0);
        ushort identity = state.NetId;
        byte owner = state.PersistentOwnerPlayerId;

        for (int operation = 0; operation < 2_000; operation++)
        {
            LiveItemState before = state;
            bool recall = random.Next(8) == 0;
            TransitionResult result;
            ItemTransition transition = null;
            RecallCommand recallCommand = null;
            if (recall)
            {
                uint expected = random.Next(5) == 0 && state.Revision > 0
                    ? state.Revision - 1
                    : state.Revision;
                recallCommand = new RecallCommand
                {
                    RequestingPlayerId = (byte)random.Next(1, 4),
                    ExpectedRevision = expected,
                    RequestedSlot = random.Next(0, 10)
                };
                result = ItemStateMachine.Recall(state, recallCommand);
            }
            else
            {
                uint expected = random.Next(5) == 0 && state.Revision > 0
                    ? state.Revision - 1
                    : state.Revision;
                transition = new ItemTransition
                {
                    // 0 is the server itself.
                    ActorPlayerId = (byte)random.Next(0, 4),
                    ExpectedRevision = expected,
                    AppliesPlacement = true,
                    RequestedPlacement = (LivePlacement)random.Next(0,
                        Enum.GetValues(typeof(LivePlacement)).Length),
                    InventoryClaimSlot = random.Next(0, 10),
                    InventoryClaimFlags = (ClaimFlags)random.Next(0, 16)
                };
                result = ItemStateMachine.Apply(state, transition);
            }

            state = result.State;
            Assert.That(state.NetId, Is.EqualTo(identity), $"identity at operation {operation}");
            Assert.That(state.PersistentOwnerPlayerId, Is.EqualTo(owner),
                $"owner at operation {operation}");
            if (state.InventoryClaimSlot != before.InventoryClaimSlot)
            {
                bool validPossessionMove = result.Accepted && transition != null &&
                    transition.ActorPlayerId == owner && transition.InventoryClaimSlot >= 0 &&
                    (transition.InventoryClaimFlags & (ClaimFlags.Reserved |
                        ClaimFlags.Locked)) != 0 &&
                    before.Placement is LivePlacement.PlayerHand or
                        LivePlacement.PlayerInventory &&
                    before.PlacementPlayerId == owner;
                bool validRecallRepair = result.Accepted && recallCommand != null &&
                    recallCommand.RequestingPlayerId == owner && recallCommand.RequestedSlot >= 0;
                Assert.That(validPossessionMove || validRecallRepair, Is.True,
                    $"claim slot move at operation {operation}");
            }
            Assert.That(state.Revision, Is.EqualTo(result.Accepted
                ? before.Revision + 1
                : before.Revision), $"revision at operation {operation}");

            bool playerPlacement = state.Placement is LivePlacement.PlayerHand or
                LivePlacement.PlayerInventory;
            Assert.That(state.PlacementPlayerId != 0, Is.EqualTo(playerPlacement),
                $"placement player at operation {operation}");
            if (state.InventoryClaimFlags.HasFlag(ClaimFlags.Stolen))
            {
                Assert.That(state.InventoryClaimFlags.HasFlag(ClaimFlags.Reserved), Is.True);
                Assert.That(state.InventoryClaimFlags.HasFlag(ClaimFlags.Dropped), Is.True);
            }
        }
    }

    [Test]
    public void InstalledItem_IsMovedOnlyByItsGadget()
    {
        LiveItemState installed = State(revision: 4, placement: LivePlacement.Installed);

        TransitionResult pickup = ItemStateMachine.Apply(installed,
            Transition(actor: 2, revision: 4, placement: LivePlacement.PlayerHand));
        ItemTransition forced = Transition(actor: ItemTransition.ServerActorId, revision: 4,
            placement: LivePlacement.LostAndFound);
        forced.Force = true;
        TransitionResult collected = ItemStateMachine.Apply(installed, forced);

        Assert.That(pickup.Accepted, Is.False);
        Assert.That(pickup.RejectionReason, Is.EqualTo("item-installed"));
        Assert.That(collected.Accepted, Is.False, "not even a forced move: its projection would stay on the car");
        Assert.That(collected.RejectionReason, Is.EqualTo("item-installed"));
    }

    [Test]
    public void Installing_NeedsAGadgetTransition()
    {
        TransitionResult result = ItemStateMachine.Apply(State(revision: 1),
            Transition(actor: 2, revision: 1, placement: LivePlacement.Installed));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("installed-only-by-gadgets"));
    }

    [Test]
    public void GadgetTransition_InstallsAndRemovesWithoutChangingTheOwner()
    {
        LiveItemState owned = State(revision: 6);
        owned.PersistentOwnerPlayerId = 5;
        ItemTransition install = Transition(actor: ItemTransition.ServerActorId, revision: 6,
            placement: LivePlacement.Installed);
        install.GadgetTransition = true;

        TransitionResult installed = ItemStateMachine.Apply(owned, install);
        ItemTransition remove = Transition(actor: ItemTransition.ServerActorId, revision: 7,
            placement: LivePlacement.World);
        remove.GadgetTransition = true;
        TransitionResult removed = ItemStateMachine.Apply(installed.State, remove);

        Assert.That(installed.Accepted, Is.True);
        Assert.That(installed.State.Placement, Is.EqualTo(LivePlacement.Installed));
        Assert.That(installed.State.PersistentOwnerPlayerId, Is.EqualTo(5));
        Assert.That(removed.Accepted, Is.True);
        Assert.That(removed.State.Placement, Is.EqualTo(LivePlacement.World));
        Assert.That(removed.State.PersistentOwnerPlayerId, Is.EqualTo(5));
    }

    [Test]
    public void InstalledItem_TakesObjectStateAndCannotBeRecalled()
    {
        LiveItemState installed = OwnedEssentialWorldItem(owner: 3, revision: 2);
        installed.Placement = LivePlacement.Installed;

        TransitionResult objectState = ItemStateMachine.Apply(installed, new ItemTransition
        {
            ActorPlayerId = ItemTransition.ServerActorId,
            ExpectedRevision = 2,
            AppliesPlacement = false
        });
        TransitionResult recall = ItemStateMachine.Recall(installed, Recall(player: 3, revision: 2, requestedSlot: 3));

        Assert.That(objectState.Accepted, Is.True);
        Assert.That(objectState.State.Placement, Is.EqualTo(LivePlacement.Installed));
        Assert.That(recall.Accepted, Is.False);
        Assert.That(recall.RejectionReason, Is.EqualTo("item-installed"));
    }

    private static LiveItemState State(ushort netId = 772, uint revision = 0,
        LivePlacement placement = LivePlacement.World) => new()
    {
        NetId = netId,
        Revision = revision,
        Placement = placement,
        InventoryClaimSlot = -1
    };

    private static LiveItemState OwnedEssentialWorldItem(byte owner, uint revision)
    {
        LiveItemState state = State(revision: revision);
        state.PersistentOwnerPlayerId = owner;
        state.InventoryClaimPlayerId = owner;
        state.InventoryClaimSlot = 3;
        state.InventoryClaimFlags = ClaimFlags.Reserved | ClaimFlags.Dropped;
        return state;
    }

    private static ItemTransition Transition(byte actor, uint revision,
        LivePlacement placement, bool retrievalClaim = false) => new()
    {
        ActorPlayerId = actor,
        ExpectedRevision = revision,
        AppliesPlacement = true,
        RequestedPlacement = placement,
        InventoryClaimSlot = retrievalClaim ? 3 : -1,
        InventoryClaimFlags = retrievalClaim ? ClaimFlags.Reserved : ClaimFlags.None
    };

    private static RecallCommand Recall(byte player, uint revision, int requestedSlot) => new()
    {
        RequestingPlayerId = player,
        ExpectedRevision = revision,
        RequestedSlot = requestedSlot
    };
}
