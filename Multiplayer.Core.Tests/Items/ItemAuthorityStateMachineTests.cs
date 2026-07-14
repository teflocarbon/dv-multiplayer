using Multiplayer.Core.Items;
using NUnit.Framework;
using System;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class ItemAuthorityStateMachineTests
{
    [Test]
    public void Store_AllowsExactlyOneCanonicalItemPerNonzeroNetId()
    {
        ItemAuthorityStore store = new();

        Assert.That(store.TryRegister(State(netId: 772)), Is.True);
        Assert.That(store.TryRegister(State(netId: 772)), Is.False);
        Assert.That(store.TryRegister(State(netId: 0)), Is.False);
        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(store.TryGet(772, out ItemAuthorityState canonical), Is.True);
        Assert.That(canonical.NetId, Is.EqualTo(772));
    }

    [Test]
    public void AcceptedTransition_HasOnePlacementAndIncreasesRevisionExactlyOnce()
    {
        ItemAuthorityState original = State(revision: 8, placement: AuthorityPlacement.World);

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original,
            Transition(actor: 2, revision: 8, placement: AuthorityPlacement.PlayerHand));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Placement, Is.EqualTo(AuthorityPlacement.PlayerHand));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(2));
        Assert.That(result.State.Revision, Is.EqualTo(9));
        Assert.That(original.Revision, Is.EqualTo(8), "the input state must remain detached/immutable");
        Assert.That(original.Placement, Is.EqualTo(AuthorityPlacement.World));
    }

    [Test]
    public void PickupByAnotherPlayer_NeverChangesPersistentOwner()
    {
        ItemAuthorityState original = OwnedEssentialWorldItem(owner: 1, revision: 12);

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original,
            Transition(actor: 2, revision: 12, placement: AuthorityPlacement.PlayerHand,
                retrievalClaim: true));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimPlayerId, Is.EqualTo(1));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Stolen), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Dropped), Is.True);
    }

    [Test]
    public void Recall_ReturnsSameCanonicalIdentityAndDoesNotCreateAnotherItem()
    {
        ItemAuthorityStore store = new();
        ItemAuthorityState borrowed = OwnedEssentialWorldItem(owner: 1, revision: 20);
        borrowed.Placement = AuthorityPlacement.PlayerHand;
        borrowed.PlacementPlayerId = 2;
        borrowed.InventoryClaimFlags |= AuthorityClaimFlags.Stolen;
        Assert.That(store.TryRegister(borrowed), Is.True);

        ItemAuthorityResult result = ItemAuthorityStateMachine.Recall(borrowed,
            Recall(player: 1, revision: 20, requestedSlot: 4));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.NetId, Is.EqualTo(borrowed.NetId));
        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(store.TryRegister(result.State), Is.False);
        Assert.That(result.State.Placement, Is.EqualTo(AuthorityPlacement.PlayerInventory));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(1));
    }

    [Test]
    public void Recall_RestoresOriginalImmovableClaimAndClearsDroppedAndStolen()
    {
        ItemAuthorityState borrowed = OwnedEssentialWorldItem(owner: 1, revision: 20);
        borrowed.InventoryClaimSlot = 3;
        borrowed.InventoryClaimFlags = AuthorityClaimFlags.Reserved |
            AuthorityClaimFlags.Dropped | AuthorityClaimFlags.Stolen;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Recall(borrowed,
            Recall(player: 1, revision: 20, requestedSlot: 9));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(3));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Reserved), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Dropped), Is.False);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Stolen), Is.False);
    }

    [Test]
    public void RecallByNonOwner_IsRejectedWithoutMutation()
    {
        ItemAuthorityState original = OwnedEssentialWorldItem(owner: 1, revision: 4);

        ItemAuthorityResult result = ItemAuthorityStateMachine.Recall(original,
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
        ItemAuthorityState original = State(revision: 7, placement: AuthorityPlacement.World);

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original,
            Transition(actor: 1, revision: 6, placement: AuthorityPlacement.PlayerHand));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("stale-authority-revision"));
        Assert.That(result.State.Revision, Is.EqualTo(7));
        Assert.That(result.State.Placement, Is.EqualTo(AuthorityPlacement.World));
    }

    [Test]
    public void CurrentPossessor_BlocksAnotherPlayersMutation()
    {
        ItemAuthorityState original = State(revision: 11, placement: AuthorityPlacement.PlayerHand);
        original.PlacementPlayerId = 1;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original,
            Transition(actor: 2, revision: 11, placement: AuthorityPlacement.World));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("sender-not-current-possessor"));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(1));
        Assert.That(result.State.Revision, Is.EqualTo(11));
    }

    [Test]
    public void RevisionsNeverWrapAround()
    {
        ItemAuthorityState original = State(revision: uint.MaxValue,
            placement: AuthorityPlacement.World);

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original,
            Transition(actor: 1, revision: uint.MaxValue,
                placement: AuthorityPlacement.PlayerHand));

        Assert.That(result.Accepted, Is.False);
        Assert.That(result.RejectionReason, Is.EqualTo("authority-revision-exhausted"));
        Assert.That(result.State.Revision, Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void FirstReservedInventoryTransition_EstablishesOwnerAndClaim()
    {
        ItemAuthorityState original = State();
        ItemTransitionCommand command = Transition(2, 0, AuthorityPlacement.PlayerInventory,
            retrievalClaim: true);
        command.InventoryClaimSlot = 5;
        command.InventoryClaimFlags = AuthorityClaimFlags.Reserved;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimPlayerId, Is.EqualTo(2));
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(5));
        Assert.That(result.State.Placement, Is.EqualTo(AuthorityPlacement.PlayerInventory));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Dropped), Is.False);
    }

    [Test]
    public void OwnerWorldTransition_RetainsReservedDroppedClaim()
    {
        ItemAuthorityState original = OwnedEssentialWorldItem(owner: 1, revision: 3);
        original.Placement = AuthorityPlacement.PlayerHand;
        original.PlacementPlayerId = 1;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original,
            Transition(1, 3, AuthorityPlacement.World, retrievalClaim: true));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PlacementPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(3));
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Reserved), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Dropped), Is.True);
        Assert.That(result.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Stolen), Is.False);
    }

    [Test]
    public void BorrowDrop_KeepsOwnerAndStolenClaimUntilRecall()
    {
        ItemAuthorityState borrowed = OwnedEssentialWorldItem(owner: 1, revision: 10);
        borrowed.Placement = AuthorityPlacement.PlayerHand;
        borrowed.PlacementPlayerId = 2;
        borrowed.InventoryClaimFlags |= AuthorityClaimFlags.Stolen;

        ItemAuthorityResult dropped = ItemAuthorityStateMachine.Apply(borrowed,
            Transition(2, 10, AuthorityPlacement.World, retrievalClaim: true));

        Assert.That(dropped.Accepted, Is.True);
        Assert.That(dropped.State.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(dropped.State.Placement, Is.EqualTo(AuthorityPlacement.World));
        Assert.That(dropped.State.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Stolen), Is.True);
    }

    [Test]
    public void RetrievalClaimSlot_CannotBeMovedByLaterOwnerTransition()
    {
        ItemAuthorityState original = OwnedEssentialWorldItem(owner: 1, revision: 6);
        ItemTransitionCommand command = Transition(1, 6, AuthorityPlacement.PlayerInventory,
            retrievalClaim: true);
        command.InventoryClaimSlot = 9;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(3));
    }

    [Test]
    public void OrdinaryInventorySlot_DoesNotEstablishRetrievalOwnership()
    {
        ItemAuthorityState original = State(revision: 6, placement: AuthorityPlacement.World);
        ItemTransitionCommand command = Transition(1, 6, AuthorityPlacement.PlayerInventory);
        command.InventoryClaimSlot = 9;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PersistentOwnerPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(-1));
    }

    [Test]
    public void ObjectOnlyUpdate_IncreasesRevisionWithoutChangingPlacement()
    {
        ItemAuthorityState original = State(revision: 2,
            placement: AuthorityPlacement.PlayerHand);
        original.PlacementPlayerId = 1;
        ItemTransitionCommand command = Transition(1, 2, AuthorityPlacement.World);
        command.AppliesPlacement = false;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Revision, Is.EqualTo(3));
        Assert.That(result.State.Placement, Is.EqualTo(AuthorityPlacement.PlayerHand));
        Assert.That(result.State.PlacementPlayerId, Is.EqualTo(1));
    }

    [Test]
    public void ForcedHostProjection_BypassesPossessorAndRevisionButNotPersistentOwnership()
    {
        ItemAuthorityState original = OwnedEssentialWorldItem(owner: 1, revision: 8);
        original.Placement = AuthorityPlacement.PlayerHand;
        original.PlacementPlayerId = 2;
        ItemTransitionCommand command = Transition(3, 1, AuthorityPlacement.World,
            retrievalClaim: true);
        command.Force = true;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Revision, Is.EqualTo(9));
        Assert.That(result.State.Placement, Is.EqualTo(AuthorityPlacement.World));
        Assert.That(result.State.PersistentOwnerPlayerId, Is.EqualTo(1));
    }

    [Test]
    public void ForcedSharedSummon_ClearsPlayerRetrievalClaim()
    {
        ItemAuthorityState original = OwnedEssentialWorldItem(owner: 2, revision: 8);
        original.Placement = AuthorityPlacement.PlayerInventory;
        original.PlacementPlayerId = 2;
        ItemTransitionCommand command = Transition(1, 0, AuthorityPlacement.World);
        command.Force = true;
        command.ClearRetrievalClaim = true;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original, command);

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.Placement, Is.EqualTo(AuthorityPlacement.World));
        Assert.That(result.State.PersistentOwnerPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimPlayerId, Is.Zero);
        Assert.That(result.State.InventoryClaimSlot, Is.EqualTo(-1));
        Assert.That(result.State.InventoryClaimFlags, Is.EqualTo(AuthorityClaimFlags.None));
    }

    [Test]
    public void Recall_RejectsStaleRevisionAndItemWithoutRetrievalClaim()
    {
        ItemAuthorityState original = OwnedEssentialWorldItem(owner: 1, revision: 8);
        ItemAuthorityResult stale = ItemAuthorityStateMachine.Recall(original,
            Recall(1, 7, 3));
        ItemAuthorityState noClaim = original.Clone();
        noClaim.InventoryClaimFlags = AuthorityClaimFlags.None;
        ItemAuthorityResult nonRecallable = ItemAuthorityStateMachine.Recall(noClaim,
            Recall(1, 8, 3));

        Assert.That(stale.RejectionReason, Is.EqualTo("stale-authority-revision"));
        Assert.That(nonRecallable.RejectionReason, Is.EqualTo("item-not-recallable"));
        Assert.That(original.Revision, Is.EqualTo(8));
    }

    [Test]
    public void TenPickupDropCycles_PreserveIdentityOwnerClaimAndMonotonicRevision()
    {
        ItemAuthorityState state = OwnedEssentialWorldItem(owner: 1, revision: 0);
        for (int cycle = 0; cycle < 10; cycle++)
        {
            ItemAuthorityResult pickup = ItemAuthorityStateMachine.Apply(state,
                Transition(2, state.Revision, AuthorityPlacement.PlayerHand, retrievalClaim: true));
            Assert.That(pickup.Accepted, Is.True, $"pickup cycle {cycle}");
            state = pickup.State;

            ItemAuthorityResult drop = ItemAuthorityStateMachine.Apply(state,
                Transition(2, state.Revision, AuthorityPlacement.World, retrievalClaim: true));
            Assert.That(drop.Accepted, Is.True, $"drop cycle {cycle}");
            state = drop.State;
        }

        Assert.That(state.NetId, Is.EqualTo(772));
        Assert.That(state.Revision, Is.EqualTo(20));
        Assert.That(state.PersistentOwnerPlayerId, Is.EqualTo(1));
        Assert.That(state.InventoryClaimSlot, Is.EqualTo(3));
        Assert.That(state.Placement, Is.EqualTo(AuthorityPlacement.World));
    }

    [TestCase(AuthorityPlacement.World)]
    [TestCase(AuthorityPlacement.Container)]
    [TestCase(AuthorityPlacement.Attached)]
    [TestCase(AuthorityPlacement.LostAndFound)]
    [TestCase(AuthorityPlacement.Installed)]
    [TestCase(AuthorityPlacement.Destroyed)]
    public void NonPlayerPlacements_AlwaysClearPlacementPlayer(AuthorityPlacement placement)
    {
        ItemAuthorityState original = State(revision: 1,
            placement: AuthorityPlacement.PlayerHand);
        original.PlacementPlayerId = 2;

        ItemAuthorityResult result = ItemAuthorityStateMachine.Apply(original,
            Transition(2, 1, placement));

        Assert.That(result.Accepted, Is.True);
        Assert.That(result.State.PlacementPlayerId, Is.Zero);
    }

    [Test]
    public void DeterministicMixedTransitionFuzz_PreservesCoreInvariants()
    {
        Random random = new(7722026);
        ItemAuthorityState state = OwnedEssentialWorldItem(owner: 1, revision: 0);
        ushort identity = state.NetId;
        byte owner = state.PersistentOwnerPlayerId;
        int claimSlot = state.InventoryClaimSlot;

        for (int operation = 0; operation < 2_000; operation++)
        {
            ItemAuthorityState before = state;
            bool recall = random.Next(8) == 0;
            ItemAuthorityResult result;
            if (recall)
            {
                uint expected = random.Next(5) == 0 && state.Revision > 0
                    ? state.Revision - 1
                    : state.Revision;
                result = ItemAuthorityStateMachine.Recall(state, new ItemRecallCommand
                {
                    RequestingPlayerId = (byte)random.Next(1, 4),
                    ExpectedRevision = expected,
                    RequestedSlot = random.Next(0, 10)
                });
            }
            else
            {
                uint expected = random.Next(5) == 0 && state.Revision > 0
                    ? state.Revision - 1
                    : state.Revision;
                result = ItemAuthorityStateMachine.Apply(state, new ItemTransitionCommand
                {
                    ActorPlayerId = (byte)random.Next(1, 4),
                    ExpectedRevision = expected,
                    AppliesPlacement = true,
                    RequestedPlacement = (AuthorityPlacement)random.Next(0, 8),
                    InventoryClaimSlot = random.Next(0, 10),
                    InventoryClaimFlags = (AuthorityClaimFlags)random.Next(0, 16)
                });
            }

            state = result.State;
            Assert.That(state.NetId, Is.EqualTo(identity), $"identity at operation {operation}");
            Assert.That(state.PersistentOwnerPlayerId, Is.EqualTo(owner),
                $"owner at operation {operation}");
            Assert.That(state.InventoryClaimSlot, Is.EqualTo(claimSlot),
                $"claim slot at operation {operation}");
            Assert.That(state.Revision, Is.EqualTo(result.Accepted
                ? before.Revision + 1
                : before.Revision), $"revision at operation {operation}");

            bool playerPlacement = state.Placement is AuthorityPlacement.PlayerHand or
                AuthorityPlacement.PlayerInventory;
            Assert.That(state.PlacementPlayerId != 0, Is.EqualTo(playerPlacement),
                $"placement player at operation {operation}");
            if (state.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Stolen))
            {
                Assert.That(state.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Reserved), Is.True);
                Assert.That(state.InventoryClaimFlags.HasFlag(AuthorityClaimFlags.Dropped), Is.True);
            }
        }
    }

    private static ItemAuthorityState State(ushort netId = 772, uint revision = 0,
        AuthorityPlacement placement = AuthorityPlacement.World) => new()
    {
        NetId = netId,
        Revision = revision,
        Placement = placement,
        InventoryClaimSlot = -1
    };

    private static ItemAuthorityState OwnedEssentialWorldItem(byte owner, uint revision)
    {
        ItemAuthorityState state = State(revision: revision);
        state.PersistentOwnerPlayerId = owner;
        state.InventoryClaimPlayerId = owner;
        state.InventoryClaimSlot = 3;
        state.InventoryClaimFlags = AuthorityClaimFlags.Reserved | AuthorityClaimFlags.Dropped;
        return state;
    }

    private static ItemTransitionCommand Transition(byte actor, uint revision,
        AuthorityPlacement placement, bool retrievalClaim = false) => new()
    {
        ActorPlayerId = actor,
        ExpectedRevision = revision,
        AppliesPlacement = true,
        RequestedPlacement = placement,
        InventoryClaimSlot = retrievalClaim ? 3 : -1,
        InventoryClaimFlags = retrievalClaim ? AuthorityClaimFlags.Reserved : AuthorityClaimFlags.None
    };

    private static ItemRecallCommand Recall(byte player, uint revision, int requestedSlot) => new()
    {
        RequestingPlayerId = player,
        ExpectedRevision = revision,
        RequestedSlot = requestedSlot
    };
}
