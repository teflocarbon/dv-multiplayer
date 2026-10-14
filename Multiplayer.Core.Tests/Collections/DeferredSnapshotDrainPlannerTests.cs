using Multiplayer.Core.Collections;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Collections;

[TestFixture]
public sealed class DeferredSnapshotDrainPlannerTests
{
    [TestCase(2u, 2u)]
    [TestCase(3u, 2u)]
    public void CurrentAndNewerSnapshotsApplyNormally(uint pending, uint current)
    {
        Assert.That(DeferredSnapshotDrainPlanner.Plan(pending, current, true).Action,
            Is.EqualTo(DeferredSnapshotDrainAction.ApplyFullSnapshot));
    }

    [Test]
    public void LegacyCreateCannotRollBackAuthoritativePlacement()
    {
        Assert.That(DeferredSnapshotDrainPlanner.Plan(0, 2, true).Action,
            Is.EqualTo(DeferredSnapshotDrainAction.ApplyTrackedStateOnly));
        Assert.That(DeferredSnapshotDrainPlanner.Plan(0, 2, false).Action,
            Is.EqualTo(DeferredSnapshotDrainAction.SkipStaleSnapshot));
    }

    [Test]
    public void StaleCreateRetainsTrackedStateWithoutRollingBackPlacement()
    {
        Assert.That(DeferredSnapshotDrainPlanner.Plan(1, 2, true).Action,
            Is.EqualTo(DeferredSnapshotDrainAction.ApplyTrackedStateOnly));
    }

    [Test]
    public void StalePlacementWithoutTrackedStateIsDiscarded()
    {
        Assert.That(DeferredSnapshotDrainPlanner.Plan(1, 2, false).Action,
            Is.EqualTo(DeferredSnapshotDrainAction.SkipStaleSnapshot));
    }
}
