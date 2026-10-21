using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

/// <summary>
/// An item whose owner isn't connected is still owned (owned-items.md, PR 2a): it carries
/// <see cref="ItemTransitionCommand.AbsentOwnerId"/>, not 0, and no player matches it.
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
        ItemAuthorityResult taken = ItemAuthorityStateMachine.Apply(Lantern(ItemTransitionCommand.AbsentOwnerId), PickUp());

        Assert.That(taken.Accepted, Is.True, "anyone may still pick it up");
        Assert.That(taken.State.PersistentOwnerPlayerId, Is.EqualTo(ItemTransitionCommand.AbsentOwnerId));
        Assert.That(taken.State.PlacementPlayerId, Is.EqualTo(Taker));
    }

    [Test]
    public void AnUnownedPersonalItem_StillBelongsToItsFirstTaker()
    {
        ItemAuthorityResult taken = ItemAuthorityStateMachine.Apply(Lantern(0), PickUp());

        Assert.That(taken.State.PersistentOwnerPlayerId, Is.EqualTo(Taker));
    }

    [Test]
    public void NobodyRecallsAnAbsentOwnersEssential()
    {
        ItemAuthorityState radio = Lantern(ItemTransitionCommand.AbsentOwnerId);
        radio.InventoryClaimSlot = 2;
        radio.InventoryClaimFlags = AuthorityClaimFlags.Reserved | AuthorityClaimFlags.Dropped;

        ItemAuthorityResult recalled = ItemAuthorityStateMachine.Recall(radio, new ItemRecallCommand
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

        Assert.That(registry.GetForOwner(ItemTransitionCommand.AbsentOwnerId), Has.Count.EqualTo(1));
        Assert.That(registry.RebindOwner("owner-a", 5), Is.EqualTo(1));
        Assert.That(registry.GetForOwner(5), Has.Count.EqualTo(1));
    }

    private static ItemAuthorityState Lantern(byte owner) => new()
    {
        NetId = 77,
        Revision = 4,
        Placement = AuthorityPlacement.World,
        PersistentOwnerPlayerId = owner
    };

    private static ItemTransitionCommand PickUp() => new()
    {
        ActorPlayerId = Taker,
        ExpectedRevision = 4,
        AppliesPlacement = true,
        RequestedPlacement = AuthorityPlacement.PlayerHand,
        MayEstablishPersistentOwner = true
    };
}
