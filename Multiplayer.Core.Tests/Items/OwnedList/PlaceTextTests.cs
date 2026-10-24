using System;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.OwnedList;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.OwnedList;

/// <summary>Where an owned item is, as its owner's list reads (owned-items.md, PR 6a).</summary>
[TestFixture]
public sealed class PlaceTextTests
{
    private static readonly Guid Owner = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = new("00000000-0000-0000-0000-0000000000b2");

    private static string Text(Home where, string holder = null, string car = null, string container = null) =>
        PlaceText.Describe(where, new PlaceFacts(Owner, holder, car, container));

    [Test]
    public void TheOwnersOwnHandsAndInventory_ReadYour()
    {
        Assert.That(Text(Home.InSlot(Owner, 3)), Is.EqualTo("in your inventory"));
        Assert.That(Text(Home.InHand(Owner)), Is.EqualTo("in your hand"));
        Assert.That(Text(Home.InSlot(Owner, 34)), Is.EqualTo("on your belt"));
    }

    [Test]
    public void AnotherPlayersHands_NameThem()
    {
        Assert.That(Text(Home.InHand(Other), holder: "Bentgear29"), Is.EqualTo("in Bentgear29's hand"));
        Assert.That(Text(Home.InSlot(Other, 2)), Is.EqualTo("in another player's inventory"));
    }

    [Test]
    public void TheWorld_SaysGroundCarOrShelf()
    {
        Assert.That(Text(Home.World), Is.EqualTo("on the ground"));
        Assert.That(Text(Home.TrainInterior("9efd01a5"), car: "L-042"), Is.EqualTo("in L-042"));
        Assert.That(Text(Home.TrainInterior("9efd01a5")), Is.EqualTo("in a car"));
        Assert.That(Text(Home.Shelf("shop-shelf-3")), Is.EqualTo("on a shelf"));
    }

    [Test]
    public void OtherPlaces_ReadPlainly()
    {
        Assert.That(Text(Home.Container(Guid.NewGuid()), container: "Toolbox"), Is.EqualTo("in Toolbox"));
        Assert.That(Text(Home.Container(Guid.NewGuid())), Is.EqualTo("in a container"));
        Assert.That(Text(Home.LostAndFound), Is.EqualTo("in Lost and Found"));
        Assert.That(Text(Home.Attached("x", true), car: "L-007"), Is.EqualTo("on the end of car L-007"));
        Assert.That(Text(Home.Attached("x", true)), Is.EqualTo("on the end of a car"));
        Assert.That(Text(Home.Installed("x"), car: "L-007"), Is.EqualTo("installed on L-007"));
        Assert.That(Text(Home.Installed(Home.WorldAnchor)), Is.EqualTo("installed in the world"));
        Assert.That(Text(Home.Snapped("hanger")), Is.EqualTo("fitted in place"));
        Assert.That(Text(Home.Magazine(Guid.NewGuid(), 0), container: "Boombox"), Is.EqualTo("loaded in Boombox"));
        Assert.That(Text(Home.Unloadable), Is.EqualTo("can't be loaded (its item mod is missing)"));
    }
}

/// <summary>What retrieving from each place means (owned-items.md, "Retrieval", PR 6b).</summary>
[TestFixture]
public sealed class RetrievalRuleTests
{
    private static readonly Guid Owner = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = new("00000000-0000-0000-0000-0000000000b2");

    [Test]
    public void EachPlace_HasItsRetrieval()
    {
        Assert.That(RetrievalRule.For(Home.InSlot(Owner, 2), Owner), Is.EqualTo(RetrievalKind.NothingToDo));
        Assert.That(RetrievalRule.For(Home.InHand(Other), Owner), Is.EqualTo(RetrievalKind.TakeFromHolder));
        Assert.That(RetrievalRule.For(Home.LostAndFound, Owner), Is.EqualTo(RetrievalKind.NothingToDo));
        Assert.That(RetrievalRule.For(Home.Unloadable, Owner), Is.EqualTo(RetrievalKind.NothingToDo));
        Assert.That(RetrievalRule.For(Home.TrainInterior("x"), Owner), Is.EqualTo(RetrievalKind.Collect));
        Assert.That(RetrievalRule.For(Home.Container(Guid.NewGuid()), Owner), Is.EqualTo(RetrievalKind.OutermostContainer));
        Assert.That(RetrievalRule.For(Home.Attached("x", true), Owner), Is.EqualTo(RetrievalKind.Collect));
        Assert.That(RetrievalRule.For(Home.Snapped("hanger"), Owner), Is.EqualTo(RetrievalKind.Collect));
        Assert.That(RetrievalRule.For(Home.Installed("x"), Owner), Is.EqualTo(RetrievalKind.NotYet));
    }
}
