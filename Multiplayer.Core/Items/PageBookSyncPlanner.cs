namespace Multiplayer.Core.Items;

public enum PageBookApplyStatus
{
    Apply,
    Defer,
    Reject
}

public readonly struct PageBookApplyPlan
{
    public PageBookApplyPlan(PageBookApplyStatus status, int requestedPage, string reason)
    {
        Status = status;
        RequestedPage = requestedPage;
        Reason = reason ?? string.Empty;
    }

    public PageBookApplyStatus Status { get; }
    public int RequestedPage { get; }
    public string Reason { get; }
}

/// <summary>Pure readiness and bounds policy for replicated PageBook state.</summary>
public static class PageBookSyncPlanner
{
    public const int MaximumSupportedPageIndex = 4095;

    public static PageBookApplyPlan Plan(int requestedPage, bool pagesGenerated, int pageCount)
    {
        if (requestedPage < 0 || requestedPage > MaximumSupportedPageIndex)
            return new PageBookApplyPlan(PageBookApplyStatus.Reject, requestedPage, "page-index-out-of-supported-range");
        if (!pagesGenerated || pageCount <= 0)
            return new PageBookApplyPlan(PageBookApplyStatus.Defer, requestedPage, "pagebook-not-generated");
        if (requestedPage >= pageCount)
            return new PageBookApplyPlan(PageBookApplyStatus.Reject, requestedPage, "page-index-out-of-range");
        return new PageBookApplyPlan(PageBookApplyStatus.Apply, requestedPage, string.Empty);
    }
}
