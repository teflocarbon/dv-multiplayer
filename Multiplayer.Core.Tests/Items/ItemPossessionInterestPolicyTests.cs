using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

public sealed class ItemPossessionInterestPolicyTests
{
    [Test]
    public void DistantForeignHolderDoesNotRetireOwnersRecallSilhouette()
    {
        Assert.That(ItemPossessionInterestPolicy.IsRelevant(ownerHasRecallClaim: true,
            placementPlayerId: 1, recipientPlayerId: 2, holderNearby: false), Is.True);
    }

    [Test]
    public void DistantUnrelatedObserverDoesNotReceiveCarriedItem()
    {
        Assert.That(ItemPossessionInterestPolicy.IsRelevant(ownerHasRecallClaim: false,
            placementPlayerId: 1, recipientPlayerId: 3, holderNearby: false), Is.False);
    }

    [TestCase(true, 1, 1)]
    [TestCase(true, 1, 3)]
    public void HolderAndNearbyObserverRemainRelevant(bool expected, byte holder, byte recipient)
    {
        bool holderNearby = recipient == 3;
        Assert.That(ItemPossessionInterestPolicy.IsRelevant(false, holder, recipient,
            holderNearby), Is.EqualTo(expected));
    }
}
