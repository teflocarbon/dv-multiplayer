using Multiplayer.Core.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Bridges PageBook's semantic page index into ordinary item tracked state. Input remains
/// entirely owned by the base game; this component only observes and applies the result.
/// </summary>
public sealed class NetworkedPageBookState : MonoBehaviour
{
    public const string TrackedValueKey = "pageBook.currentPage";

    private NetworkedItem networkedItem;
    private PageBook pageBook;
    private bool registered;
    private bool applyingNetworkPage;
    private int logicalPage;
    private int? pendingPage;

    public int LogicalPage => logicalPage;
    public int? PendingPage => pendingPage;
    public bool PagesReady => IsReady();

    public static NetworkedPageBookState TryRegister(NetworkedItem item, PageBook knownPageBook = null)
    {
        if (item == null) return null;
        PageBook target = knownPageBook ?? item.GetComponentInChildren<PageBook>(true);
        if (target == null) return null;
        NetworkedPageBookState adapter = item.GetComponent<NetworkedPageBookState>() ??
            item.gameObject.AddComponent<NetworkedPageBookState>();
        adapter.Initialize(item, target);
        return adapter;
    }

    private void Initialize(NetworkedItem item, PageBook target)
    {
        if (registered) return;
        networkedItem = item;
        pageBook = target;
        logicalPage = pageBook.currentPage;
        pageBook.PageFlipped += OnPageFlipped;
        pageBook.PageBookGenerated += OnPageBookGenerated;
        networkedItem.RegisterTrackedValue(TrackedValueKey, () => logicalPage, ApplyNetworkPage);
        registered = true;
        NetworkedItemManager.Instance?.ScheduleTrackedValueFinalization(networkedItem);
        Publish("item.pagebook-registered", new()
        {
            ["currentPage"] = logicalPage,
            ["pageCount"] = pageBook.PageNum,
            ["pagesGenerated"] = pageBook.PagesGenerated,
            ["runtimePageCount"] = pageBook.pages?.Count ?? 0
        });
    }

    private void OnDestroy()
    {
        if (pageBook == null) return;
        pageBook.PageFlipped -= OnPageFlipped;
        pageBook.PageBookGenerated -= OnPageBookGenerated;
    }

    private void OnPageFlipped(int page)
    {
        int previous = logicalPage;
        logicalPage = pageBook.currentPage;
        if (applyingNetworkPage) return;
        Publish("item.page-change-observed", new()
        {
            ["source"] = "local-input-or-initialization",
            ["previousPage"] = previous,
            ["eventPage"] = page,
            ["currentPage"] = logicalPage,
            ["pageCount"] = pageBook.PageNum,
            ["pagesGenerated"] = pageBook.PagesGenerated
        });
    }

    private void OnPageBookGenerated()
    {
        Publish("item.pagebook-generated", new()
        {
            ["currentPage"] = pageBook.currentPage,
            ["pageCount"] = pageBook.PageNum,
            ["runtimePageCount"] = pageBook.pages?.Count ?? 0,
            ["pendingPage"] = pendingPage
        });
        if (!pendingPage.HasValue)
        {
            logicalPage = pageBook.currentPage;
            return;
        }
        try { ApplyReadyPage(pendingPage.Value, "generation-complete"); }
        catch (Exception exception)
        {
            Publish("item.page-apply.exception", new()
            {
                ["requestedPage"] = pendingPage,
                ["exceptionType"] = exception.GetType().FullName,
                ["message"] = exception.Message
            }, DebugSeverity.Error);
        }
    }

    private void ApplyNetworkPage(int requestedPage)
    {
        // Change the logical getter immediately. TrackedValue records the incoming value as its
        // clean baseline after this setter returns, preventing a deferred state from echoing the
        // old visual page while asynchronous texture generation is still running.
        logicalPage = requestedPage;
        PageBookApplyPlan plan = PageBookSyncPlanner.Plan(requestedPage, pageBook.PagesGenerated, pageBook.PageNum);
        if (plan.Status == PageBookApplyStatus.Defer)
        {
            pendingPage = requestedPage;
            DebugDiagnostics.ReportDependency("Item", networkedItem.NetId.ToString(), "pagebook-not-generated",
                $"requestedPage={requestedPage}");
            Publish("item.page-apply-deferred", State(requestedPage, plan.Reason), DebugSeverity.Warning);
            return;
        }
        if (plan.Status == PageBookApplyStatus.Reject)
        {
            pendingPage = null;
            Publish("item.page-index-invalid", State(requestedPage, plan.Reason), DebugSeverity.Error);
            return;
        }
        ApplyReadyPage(requestedPage, "tracked-state");
    }

    private void ApplyReadyPage(int requestedPage, string source)
    {
        PageBookApplyPlan plan = PageBookSyncPlanner.Plan(requestedPage, pageBook.PagesGenerated, pageBook.PageNum);
        if (plan.Status != PageBookApplyStatus.Apply)
        {
            if (plan.Status == PageBookApplyStatus.Defer) pendingPage = requestedPage;
            Publish(plan.Status == PageBookApplyStatus.Defer ? "item.page-apply-deferred" : "item.page-index-invalid",
                State(requestedPage, plan.Reason), plan.Status == PageBookApplyStatus.Defer ? DebugSeverity.Warning : DebugSeverity.Error);
            return;
        }

        int previous = pageBook.currentPage;
        Publish("item.page-apply.before", State(requestedPage, source));
        applyingNetworkPage = true;
        try
        {
            pageBook.ForceCurrentPage(requestedPage);
            logicalPage = pageBook.currentPage;
            pendingPage = null;
            DebugDiagnostics.ResolveDependency("Item", networkedItem.NetId.ToString(), "pagebook-not-generated");
        }
        finally { applyingNetworkPage = false; }
        Dictionary<string, object> after = State(requestedPage, source);
        after["previousPage"] = previous;
        after["appliedPage"] = pageBook.currentPage;
        Publish(source == "generation-complete" ? "item.page-apply-drained" : "item.page-apply.after", after);
    }

    private bool IsReady() => pageBook != null && pageBook.PagesGenerated && pageBook.PageNum > 0;

    private Dictionary<string, object> State(int requestedPage, string reason) => new()
    {
        ["requestedPage"] = requestedPage,
        ["currentPage"] = pageBook?.currentPage ?? -1,
        ["logicalPage"] = logicalPage,
        ["pendingPage"] = pendingPage,
        ["pageCount"] = pageBook?.PageNum ?? 0,
        ["runtimePageCount"] = pageBook?.pages?.Count ?? 0,
        ["pagesGenerated"] = pageBook?.PagesGenerated ?? false,
        ["reason"] = reason ?? string.Empty,
        ["authorityRevision"] = networkedItem?.AuthorityRevision ?? 0
    };

    private void Publish(string eventName, Dictionary<string, object> data, DebugSeverity severity = DebugSeverity.Info)
    {
        if (!DebugRuntime.EnabledFor("item")) return;
        DebugRuntime.Publish("item", eventName,
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            severity, "Item", networkedItem?.NetId.ToString() ?? "0", data);
    }
}
