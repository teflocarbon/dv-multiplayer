using System;
using System.Collections.Generic;
using DV.Booklets;
using DV.Logic.Job;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Jobs;

/// <summary>
/// Job papers as rows (docs/design/items/inventory.md, "Job papers"; D14, OI-COLD-07, OI-COLD-15).
/// A booklet, an overview or a report in an inventory is a row like any item, on the server and in
/// its holder's game, and its object is made again from its job (<c>BookletCreator</c>) under the
/// same NetId when it leaves the slot. The row's state says which: a booklet's own (vanilla saves
/// its <c>jobId</c>), an overview's job added here, and a report's render recipe (its artifact,
/// <see cref="JobReportArtifactData"/>), which no job keeps once it's over.
/// </summary>
internal static class JobPapers
{
    private const string JobIdKey = "jobId";
    private const string ReportKey = "mpJobReport";
    private const string ReportJobKey = "mpReportJobId";

    // Each report's recipe by NetId, on every game that has seen it, so it goes with its row.
    private static readonly Dictionary<ushort, string> reports = new();

    /// <summary>While a paper is made from its row: the server's BookletCreator patches leave it alone.</summary>
    public static bool MakingFromRow { get; private set; }

    /// <summary>Whether items of this prefab are job papers the job system makes.</summary>
    public static bool IsPaper(string prefabName) => NetworkedItemManager.IsJobDocumentPrefab(prefabName);

    /// <summary>A report's recipe, kept for its row.</summary>
    public static void KeepReport(ushort netId, JobReportArtifactData artifact)
    {
        if (netId != 0 && artifact != null)
            reports[netId] = Convert.ToBase64String(artifact.Serialize());
    }

    /// <summary>Whether a report's row has its recipe (one saved before C7 has none, and can't be made).</summary>
    public static bool HasRecipe(JObject state) => state?[ReportKey] is { Type: JTokenType.String };

    /// <summary>The job a paper's row is for, or null.</summary>
    public static string JobIdOf(JObject state) => (string)state?[JobIdKey];

    public static string JobIdOf(string state)
    {
        if (string.IsNullOrEmpty(state))
            return null;
        try
        {
            return JobIdOf(JObject.Parse(state));
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A paper's state completed for its row: an overview's job, which it doesn't save, and a
    /// report's recipe. Any other item's state as it is.
    /// </summary>
    public static JObject Complete(GameObject item, JObject state)
    {
        if (item == null)
            return state;
        if (item.GetComponent<JobOverview>() is { job: { } overviewJob })
            (state ??= new JObject())[JobIdKey] = overviewJob.ID;
        else if (item.GetComponent<JobBooklet>() is { job: { } bookletJob } && JobIdOf(state) == null)
            (state ??= new JObject())[JobIdKey] = bookletJob.ID;
        else if (item.GetComponent<JobReport>() is { } report && item.GetComponent<NetworkedItem>() is { } networked &&
                 reports.TryGetValue(networked.NetId, out string recipe))
        {
            (state ??= new JObject())[ReportKey] = recipe;
            state[ReportJobKey] = report.jobID;
        }
        return state;
    }

    /// <summary>
    /// The job id a paper's stand-in is named after: its job's, or a report's own (kept apart, as
    /// a report outlives its job and no job takes it back).
    /// </summary>
    public static string NameOf(JObject state) => JobIdOf(state) ?? (string)state?[ReportJobKey];

    /// <summary>
    /// A paper's object made from its row, at the safety position and not yet bound to a NetId
    /// (<see cref="Track"/> does that), or null if its job (or recipe) isn't known here.
    /// </summary>
    public static Component Make(string prefabName, JObject state, Vector3 position, Quaternion rotation)
    {
        MakingFromRow = true;
        try
        {
            switch (prefabName)
            {
                case nameof(JobBooklet):
                    return JobOf(state) is { } bookletJob
                        ? BookletCreator.CreateJobBooklet(bookletJob, position, rotation, WorldMover.OriginShiftParent, true)
                        : null;
                case nameof(JobOverview):
                    return JobOf(state) is { } overviewJob
                        ? BookletCreator.CreateJobOverview(overviewJob, position, rotation, WorldMover.OriginShiftParent)
                        : null;
                case nameof(JobReport):
                    if ((string)state?[ReportKey] is not { } recipe)
                        return null;
                    JobReportArtifactData artifact = JobReportArtifactData.Deserialize(Convert.FromBase64String(recipe));
                    return BookletCreator_JobReport.Create(artifact.Job.ToGameData(), artifact.Debt?.ToGameData(),
                        position, rotation, WorldMover.OriginShiftParent);
                default:
                    return null;
            }
        }
        catch (Exception exception)
        {
            Multiplayer.LogWarning($"Job papers: {prefabName} couldn't be made from its row: {exception.Message}");
            return null;
        }
        finally
        {
            MakingFromRow = false;
        }
    }

    /// <summary>Whether this paper can be a row: its job is known (a report: its recipe), so it can be made again.</summary>
    public static bool CanBeRow(GameObject item) => PaperOf(item) switch
    {
        JobBooklet booklet => booklet.job != null,
        JobOverview overview => overview.job != null,
        JobReport => item.TryGetComponent(out NetworkedItem networked) && reports.ContainsKey(networked.NetId),
        _ => false
    };

    /// <summary>The object's job paper (booklet, overview or report), or null.</summary>
    public static Component PaperOf(GameObject item)
    {
        if (item == null)
            return null;
        if (item.TryGetComponent(out JobBooklet booklet))
            return booklet;
        if (item.TryGetComponent(out JobOverview overview))
            return overview;
        return item.TryGetComponent(out JobReport report) ? report : null;
    }

    private static Job JobOf(JObject state) =>
        JobIdOf(state) is { } jobId && NetworkedJob.TryGetFromJobId(jobId, out NetworkedJob job) ? job.Job : null;

    /// <summary>
    /// A paper's object takes up its NetId as the job system's own paper (its tracked item is the
    /// paper, so it's never made generically), and joins its job: the overview, a booklet copy, a
    /// report. A report's recipe is kept under its NetId, and the server sends it before the item.
    /// A report's artifact, with its NetId, which the server registers; otherwise null.
    /// </summary>
    public static JobReportArtifactData Track(NetworkedItem item, Component paper, ushort netId, bool createDirty, JObject state)
    {
        NetworkedJob job = null;
        JobReportArtifactData artifact = null;
        switch (paper)
        {
            case JobBooklet booklet:
                item.Initialize(booklet, netId, createDirty);
                if (NetworkedJob.TryGetFromJob(booklet.job, out job))
                    job.RegisterJobBooklet(item, 0);
                break;
            case JobOverview overview:
                item.Initialize(overview, netId, createDirty);
                if (NetworkedJob.TryGetFromJob(overview.job, out job))
                    job.JobOverview = item;
                break;
            case JobReport report:
                item.Initialize(report, netId, createDirty);
                if ((string)state?[ReportKey] is { } recipe)
                {
                    reports[item.NetId] = recipe;
                    artifact = JobReportArtifactData.Deserialize(Convert.FromBase64String(recipe));
                    artifact.ItemNetId = item.NetId;
                    if (NetworkedJob.Get(artifact.JobNetId, out job) && job?.Job?.ID == artifact.Job?.Id)
                        job.AddReport(item);
                }
                break;
        }
        return artifact;
    }
}
