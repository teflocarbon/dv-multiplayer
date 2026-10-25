using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Projection;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Projection;

[TestFixture]
public sealed class WorldItemIndexingTests
{
    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.StaticParent)]
    public void AnItemOnTheGroundOrAShelfIsFoundByItsCell(LivePlacement placement) =>
        Assert.That(WorldItemIndexing.IndexedByCell(placement), Is.True);

    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.Installed)]
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.SnappedAttachment)]
    [TestCase(LivePlacement.Magazine)]
    [TestCase(LivePlacement.LostAndFound)]
    public void NothingElseIs(LivePlacement placement) =>
        Assert.That(WorldItemIndexing.IndexedByCell(placement), Is.False);
}
