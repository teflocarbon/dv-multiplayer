using Multiplayer.Core.Items.WorldItems;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.WorldItems;

[TestFixture]
public sealed class StaticParentKeysTests
{
    [Test]
    public void ItIsTheShelfsPathByName() =>
        Assert.That(StaticParentKeys.Material(new[] { "Shed_content", "Interior", "ItemStaticParent" }),
            Is.EqualTo("static-parent-v2/Shed_content/Interior/ItemStaticParent"));

    [Test]
    public void TheRootContentIsMovedUnderIsNotPartOfIt() =>
        // Under it, a tile's place among the others is the order the tiles loaded in (SH1).
        Assert.That(StaticParentKeys.Material(new[] { StaticParentKeys.OriginShiftRoot, "Shed_content", "ItemStaticParent" }),
            Is.EqualTo(StaticParentKeys.Material(new[] { "Shed_content", "ItemStaticParent" })));

    [Test]
    public void OnlyAsItsRoot() =>
        Assert.That(StaticParentKeys.Material(new[] { "A", StaticParentKeys.OriginShiftRoot }),
            Does.Contain(StaticParentKeys.OriginShiftRoot));

    [Test]
    public void TheNearestOfTwoWithOneKey()
    {
        Assert.That(StaticParentKeys.Nearest(new[] { 400f, 9f, 100f }), Is.EqualTo(1));
        Assert.That(StaticParentKeys.Nearest(new float[0]), Is.EqualTo(-1));
    }
}
