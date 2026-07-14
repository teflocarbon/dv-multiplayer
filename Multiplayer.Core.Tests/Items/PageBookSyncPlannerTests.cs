using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class PageBookSyncPlannerTests
{
    [TestCase(0)]
    [TestCase(4)]
    [TestCase(60)]
    public void AppliesValidGeneratedPage(int page)
    {
        PageBookApplyPlan result = PageBookSyncPlanner.Plan(page, true, 61);
        Assert.That(result.Status, Is.EqualTo(PageBookApplyStatus.Apply));
        Assert.That(result.RequestedPage, Is.EqualTo(page));
    }

    [TestCase(false, 0)]
    [TestCase(false, 61)]
    [TestCase(true, 0)]
    public void DefersUntilGeneratedPageCountExists(bool generated, int pageCount)
    {
        PageBookApplyPlan result = PageBookSyncPlanner.Plan(4, generated, pageCount);
        Assert.That(result.Status, Is.EqualTo(PageBookApplyStatus.Defer));
        Assert.That(result.Reason, Is.EqualTo("pagebook-not-generated"));
    }

    [TestCase(-1, 61, "page-index-out-of-supported-range")]
    [TestCase(4096, 5000, "page-index-out-of-supported-range")]
    [TestCase(61, 61, "page-index-out-of-range")]
    public void RejectsInvalidPage(int page, int pageCount, string reason)
    {
        PageBookApplyPlan result = PageBookSyncPlanner.Plan(page, true, pageCount);
        Assert.That(result.Status, Is.EqualTo(PageBookApplyStatus.Reject));
        Assert.That(result.Reason, Is.EqualTo(reason));
    }
}
