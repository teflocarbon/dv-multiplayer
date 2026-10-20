using System.Collections.Generic;
using Multiplayer.Core.Shops;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Shops;

[TestFixture]
public sealed class ShopCatalogueFingerprintTests
{
    private static readonly string[] Items = { "shovel", "lighter", "Boombox", "WirelessMUController" };

    private static IReadOnlyList<IReadOnlyList<string>> Shops(params string[][] shops) => shops;

    [Test]
    public void SameShopsMatch()
    {
        ShopCatalogueFingerprint host = ShopCatalogueFingerprint.Compute(Items, Shops(new[] { "shovel", "lighter" }, new[] { "Boombox" }));
        ShopCatalogueFingerprint client = ShopCatalogueFingerprint.Compute((string[])Items.Clone(), Shops(new[] { "shovel", "lighter" }, new[] { "Boombox" }));

        Assert.That(client, Is.EqualTo(host));
        Assert.That(host.DescribeMismatch(client), Is.Null);
        Assert.That((host.Shops, host.Items), Is.EqualTo((2, 4)));
    }

    [Test]
    public void OrderWithinAShopCountsAndSaysTheCountsAgree()
    {
        ShopCatalogueFingerprint host = ShopCatalogueFingerprint.Compute(Items, Shops(new[] { "shovel", "lighter" }));
        ShopCatalogueFingerprint client = ShopCatalogueFingerprint.Compute(Items, Shops(new[] { "lighter", "shovel" }));

        Assert.That(client.Hash, Is.Not.EqualTo(host.Hash));
        Assert.That(host.DescribeMismatch(client), Does.Contain("not in the same order"));
    }

    [Test]
    public void AnExtraItemSaysBothCounts()
    {
        ShopCatalogueFingerprint host = ShopCatalogueFingerprint.Compute(Items, Shops(new[] { "shovel" }));
        ShopCatalogueFingerprint client = ShopCatalogueFingerprint.Compute(new[] { "shovel", "lighter", "Boombox", "WirelessMUController", "ModdedThing" }, Shops(new[] { "shovel" }));

        string reason = host.DescribeMismatch(client);
        Assert.That(reason, Does.Contain("The host has 1 shops and 4 shop items; you have 1 shops and 5 shop items."));
    }

    [Test]
    public void ItemsMovedBetweenShopsDontMatch()
    {
        ShopCatalogueFingerprint host = ShopCatalogueFingerprint.Compute(Items, Shops(new[] { "shovel", "lighter" }, new[] { "Boombox" }));
        ShopCatalogueFingerprint client = ShopCatalogueFingerprint.Compute(Items, Shops(new[] { "shovel" }, new[] { "lighter", "Boombox" }));

        Assert.That(client.Hash, Is.Not.EqualTo(host.Hash));
    }

    [Test]
    public void NameBoundariesCount()
    {
        ShopCatalogueFingerprint a = ShopCatalogueFingerprint.Compute(new[] { "ab", "c" }, Shops());
        ShopCatalogueFingerprint b = ShopCatalogueFingerprint.Compute(new[] { "a", "bc" }, Shops());

        Assert.That(a.Hash, Is.Not.EqualTo(b.Hash));
    }

    [Test]
    public void TheHashDoesntDependOnTheProcess()
    {
        ShopCatalogueFingerprint fingerprint = ShopCatalogueFingerprint.Compute(new[] { "shovel" }, Shops(new[] { "shovel" }));

        Assert.That(fingerprint.Hash, Is.EqualTo(ShopCatalogueFingerprint.Compute(new[] { "shovel" }, Shops(new[] { "shovel" })).Hash));
        Assert.That(fingerprint.Hash, Is.EqualTo(ExpectedShovelHash));
    }

    // FNV-1a 64 of the encoding in ShopCatalogueFingerprint; changing the encoding changes this.
    private const ulong ExpectedShovelHash = 12400717981611680459UL;
}
