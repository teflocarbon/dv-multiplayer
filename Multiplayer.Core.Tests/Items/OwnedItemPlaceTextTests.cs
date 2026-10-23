using System;
using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

/// <summary>Where an owned item is, as its owner's list reads (owned-items.md, PR 6a).</summary>
[TestFixture]
public sealed class OwnedItemPlaceTextTests
{
    private static readonly Guid Owner = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = new("00000000-0000-0000-0000-0000000000b2");

    private static string Text(OwnedItemWhere where, string holder = null, string car = null, string container = null) =>
        OwnedItemPlaceText.Describe(where, new OwnedItemPlaceFacts(Owner, holder, car, container));

    [Test]
    public void TheOwnersOwnHandsAndInventory_ReadYour()
    {
        Assert.That(Text(OwnedItemWhere.With(Owner, OwnedItemHold.Inventory, 3)), Is.EqualTo("in your inventory"));
        Assert.That(Text(OwnedItemWhere.With(Owner, OwnedItemHold.Hand)), Is.EqualTo("in your hand"));
        Assert.That(Text(OwnedItemWhere.With(Owner, OwnedItemHold.Belt, 30)), Is.EqualTo("on your belt"));
    }

    [Test]
    public void AnotherPlayersHands_NameThem()
    {
        Assert.That(Text(OwnedItemWhere.With(Other, OwnedItemHold.Hand), holder: "Bentgear29"), Is.EqualTo("in Bentgear29's hand"));
        Assert.That(Text(OwnedItemWhere.With(Other, OwnedItemHold.Inventory)), Is.EqualTo("in another player's inventory"));
    }

    [Test]
    public void TheWorld_SaysGroundCarOrShelf()
    {
        Assert.That(Text(OwnedItemWhere.World()), Is.EqualTo("on the ground"));
        Assert.That(Text(OwnedItemWhere.World("car:9efd01a5"), car: "L-042"), Is.EqualTo("in L-042"));
        Assert.That(Text(OwnedItemWhere.World("car:9efd01a5")), Is.EqualTo("in a car"));
        Assert.That(Text(OwnedItemWhere.World("static:shop-shelf-3")), Is.EqualTo("on a shelf"));
    }

    [Test]
    public void OtherPlaces_ReadPlainly()
    {
        Assert.That(Text(OwnedItemWhere.Container(Guid.NewGuid()), container: "Toolbox"), Is.EqualTo("in Toolbox"));
        Assert.That(Text(OwnedItemWhere.Container(Guid.NewGuid())), Is.EqualTo("in a container"));
        Assert.That(Text(OwnedItemWhere.LostAndFound), Is.EqualTo("in Lost and Found"));
        Assert.That(Text(OwnedItemWhere.Attached("car:x"), car: "L-007"), Is.EqualTo("attached to L-007"));
        Assert.That(Text(OwnedItemWhere.Installed("car:x", 4), car: "L-007"), Is.EqualTo("installed on L-007"));
        Assert.That(Text(OwnedItemWhere.Missing), Is.EqualTo("missing"));
    }
}
