using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.LostAndFound;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.LiveState;

/// <summary>
/// An item whose owner isn't connected is still owned (owned-items.md, PR 2a): it carries
/// <see cref="ItemTransition.AbsentOwnerId"/>, not 0, and no player matches it.
/// </summary>
[TestFixture]
public sealed class AbsentOwnerTests
{
    private const byte Taker = 3;

    [Test]
    public void TakingAnAbsentOwnersPersonalItem_DoesntMakeTheTakerItsOwner()
    {
        // Before: an offline owner's number was 0, so the next player to take it became its owner
        // (item-ownership-open-questions.md, "Known gap").
        TransitionResult taken = ItemStateMachine.Apply(Lantern(ItemTransition.AbsentOwnerId), PickUp());

        Assert.That(taken.Accepted, Is.True, "anyone may still pick it up");
        Assert.That(taken.State.PersistentOwnerPlayerId, Is.EqualTo(ItemTransition.AbsentOwnerId));
        Assert.That(taken.State.PlacementPlayerId, Is.EqualTo(Taker));
    }

    [Test]
    public void AnUnownedPersonalItem_StillBelongsToItsFirstTaker()
    {
        TransitionResult taken = ItemStateMachine.Apply(Lantern(0), PickUp());

        Assert.That(taken.State.PersistentOwnerPlayerId, Is.EqualTo(Taker));
    }

    [Test]
    public void NobodyRecallsAnAbsentOwnersEssential()
    {
        LiveItemState radio = Lantern(ItemTransition.AbsentOwnerId);
        radio.InventoryClaimSlot = 2;
        radio.InventoryClaimFlags = ClaimFlags.Reserved | ClaimFlags.Dropped;

        TransitionResult recalled = ItemStateMachine.Recall(radio, new RecallCommand
        {
            RequestingPlayerId = Taker,
            ExpectedRevision = radio.Revision,
            RequestedSlot = 2
        });

        Assert.That(recalled.Accepted, Is.False);
        Assert.That(recalled.RejectionReason, Is.EqualTo("requester-not-persistent-owner"));
    }

    [Test]
    public void AnOfflineOwnersLostAndFoundEntry_KeepsAnOwner()
    {
        LostItemRegistry registry = new();
        registry.Add(new LostItemRecord
        {
            PersistentItemId = System.Guid.NewGuid(),
            NetId = 42,
            Revision = 7,
            OwnerPlayerId = 1,
            OwnerIdentity = "owner-a"
        });

        registry.UnbindOwner(1);

        Assert.That(registry.GetForOwner(ItemTransition.AbsentOwnerId), Has.Count.EqualTo(1));
        Assert.That(registry.RebindOwner("owner-a", 5), Is.EqualTo(1));
        Assert.That(registry.GetForOwner(5), Has.Count.EqualTo(1));
    }

    private static LiveItemState Lantern(byte owner) => new()
    {
        NetId = 77,
        Revision = 4,
        Placement = LivePlacement.World,
        PersistentOwnerPlayerId = owner
    };

    private static ItemTransition PickUp() => new()
    {
        ActorPlayerId = Taker,
        ExpectedRevision = 4,
        AppliesPlacement = true,
        RequestedPlacement = LivePlacement.PlayerHand,
        MayEstablishPersistentOwner = true
    };
}
