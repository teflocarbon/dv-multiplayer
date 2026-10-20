using System.Linq;
using Multiplayer.Core.Shops;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Shops;

[TestFixture]
public sealed class ShopShelfSeedTests
{
    [Test]
    public void SameSessionAndShopPickTheSameSeed()
    {
        Assert.That(ShopShelfSeed.Pick(123456, 3, 20), Is.EqualTo(ShopShelfSeed.Pick(123456, 3, 20)));
    }

    [Test]
    public void PicksStayInRange()
    {
        for (int session = -50; session < 50; session++)
            for (int shop = 0; shop < 12; shop++)
                Assert.That(ShopShelfSeed.Pick(session * 7919, shop, 7), Is.InRange(0, 6));
    }

    [Test]
    public void NoSeedsPicksZero()
    {
        Assert.That(ShopShelfSeed.Pick(42, 1, 0), Is.EqualTo(0));
    }

    [Test]
    public void SessionsAndShopsSpreadOverTheSeeds()
    {
        int[] bySession = Enumerable.Range(0, 200).Select(session => ShopShelfSeed.Pick(session, 0, 10)).ToArray();
        int[] byShop = Enumerable.Range(0, 200).Select(shop => ShopShelfSeed.Pick(77, shop, 10)).ToArray();

        Assert.That(bySession.Distinct().Count(), Is.EqualTo(10), "every seed used across sessions");
        Assert.That(byShop.Distinct().Count(), Is.EqualTo(10), "every seed used across shops");
    }
}
