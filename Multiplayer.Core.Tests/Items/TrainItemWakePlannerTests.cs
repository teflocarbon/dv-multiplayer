using Multiplayer.Core.Items;
using NUnit.Framework;
using System.Linq;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class TrainItemWakePlannerTests
{
    [Test]
    public void RemovingBottomPropagatesUpwardThroughStack()
    {
        TrainItemWakeNode[] nodes =
        {
            Node(2, 10, 1.5f),
            Node(3, 10, 2.5f),
            Node(4, 10, 3.5f)
        };

        ushort[] wake = TrainItemWakePlanner.FromRemovedSupport(nodes, 10,
            Bounds(0.5f), verticalTolerance: 0.02f);

        Assert.That(wake, Is.EqualTo(new ushort[] { 2, 3, 4 }));
    }

    [Test]
    public void SideBySideAndOtherCarItemsRemainSettled()
    {
        TrainItemWakeNode[] nodes =
        {
            Node(2, 10, 1.5f),
            new(3, 10, true, new TrainItemWakeBounds(2f, 1.5f, 0f, 0.45f, 0.5f, 0.45f)),
            Node(4, 11, 1.5f)
        };

        ushort[] wake = TrainItemWakePlanner.FromRemovedSupport(nodes, 10,
            Bounds(0.5f), verticalTolerance: 0.02f);

        Assert.That(wake, Is.EqualTo(new ushort[] { 2 }));
    }

    [Test]
    public void ImpactIncludesSeedAndObjectsItSupports()
    {
        TrainItemWakeNode[] nodes =
        {
            Node(2, 10, 0.5f),
            Node(3, 10, 1.5f),
            Node(4, 10, 2.5f)
        };

        ushort[] wake = TrainItemWakePlanner.FromSeeds(nodes, 10, new ushort[] { 2 },
            verticalTolerance: 0.02f);

        Assert.That(wake, Is.EqualTo(new ushort[] { 2, 3, 4 }));
    }

    [Test]
    public void CascadeHonoursItemBudget()
    {
        TrainItemWakeNode[] nodes = Enumerable.Range(1, 12)
            .Select(index => Node((ushort)index, 10, index - 0.5f)).ToArray();

        ushort[] wake = TrainItemWakePlanner.FromRemovedSupport(nodes, 10,
            Bounds(-0.5f), maximumItems: 4, verticalTolerance: 0.02f);

        Assert.That(wake, Has.Length.EqualTo(4));
        Assert.That(wake, Is.EqualTo(new ushort[] { 1, 2, 3, 4 }));
    }

    private static TrainItemWakeNode Node(ushort id, ushort car, float y) =>
        new(id, car, true, Bounds(y));

    private static TrainItemWakeBounds Bounds(float y) =>
        new(0f, y, 0f, 0.45f, 0.5f, 0.45f);
}
