using System;
using Multiplayer.Core.Items.Projection;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Projection;

public sealed class ItemPossessionInterestPolicyTests
{
    private static readonly Guid PlayerOne = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid PlayerTwo = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid PlayerThree = new("00000000-0000-0000-0000-000000000003");

    [Test]
    public void DistantForeignHolderDoesNotRetireOwnersRecallSilhouette()
    {
        Assert.That(ItemPossessionInterestPolicy.IsRelevant(ownerHasRecallClaim: true,
            holder: PlayerOne, recipient: PlayerTwo, holderNearby: false), Is.True);
    }

    [Test]
    public void DistantUnrelatedObserverDoesNotReceiveCarriedItem()
    {
        Assert.That(ItemPossessionInterestPolicy.IsRelevant(ownerHasRecallClaim: false,
            holder: PlayerOne, recipient: PlayerThree, holderNearby: false), Is.False);
    }

    [Test]
    public void NobodyHoldingIsNotTheRecipient()
    {
        Assert.That(ItemPossessionInterestPolicy.IsRelevant(false, Guid.Empty, Guid.Empty, false), Is.False);
    }

    [TestCase(1)]
    [TestCase(3)]
    public void HolderAndNearbyObserverRemainRelevant(int recipientNumber)
    {
        Guid recipient = recipientNumber == 1 ? PlayerOne : PlayerThree;
        bool holderNearby = recipient == PlayerThree;
        Assert.That(ItemPossessionInterestPolicy.IsRelevant(false, PlayerOne, recipient,
            holderNearby), Is.True);
    }
}
