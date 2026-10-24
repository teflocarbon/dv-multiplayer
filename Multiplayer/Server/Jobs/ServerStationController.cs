using DV.Booklets;
using DV.Logic.Job;
using DV.Utils;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// The server's part of a station (beside its <see cref="NetworkedStationController"/>): gives each
/// of the station's jobs a <see cref="NetworkedJob"/>, and each tick sends the new jobs and the
/// ones whose state changed. Removes itself on a game that isn't the server.
/// </summary>
public class ServerStationController : MonoBehaviour
{
    // Jobs added to a station before its networked controller was ready.
    private static readonly Dictionary<Station, HashSet<Job>> pendingJobs = [];

    private NetworkedStationController station;
    private bool ready;
    private readonly List<NetworkedJob> newJobs = [];
    private readonly List<NetworkedJob> dirtyJobs = [];

    /// <summary>Registers a job the station has just been given, or keeps it until the station is ready.</summary>
    public static void RegisterOrQueueJob(Station station, Job job)
    {
        if (station == null || job == null)
            return;
        if (NetworkedStationController.GetFromStation(station, out NetworkedStationController controller) &&
            controller != null && controller.TryGetComponent(out ServerStationController server) && server.ready)
        {
            server.AddJob(job);
            return;
        }

        if (!pendingJobs.TryGetValue(station, out HashSet<Job> jobs))
            pendingJobs[station] = jobs = [];
        jobs.Add(job);
        DebugRuntime.Publish("job", "job.station-registration-deferred", DebugRuntimeSide.Server,
            DebugSeverity.Warning, "Station", station.ID ?? string.Empty, new()
            {
                ["jobId"] = job.ID ?? string.Empty,
                ["pendingCount"] = jobs.Count,
                ["reason"] = "networked-station-not-ready"
            });
    }

    protected void Awake()
    {
        station = GetComponent<NetworkedStationController>();
    }

    protected void Start()
    {
        if (station == null || !NetworkLifecycle.Instance.IsHost())
        {
            Destroy(this);
            return;
        }

        NetworkLifecycle.Instance.OnTick += OnTick;
        if (station.IsRegistered)
            OnStationRegistered();
        else
            station.Registered += OnStationRegistered;
    }

    protected void OnDisable()
    {
        if (UnloadWatcher.isQuitting)
            return;

        if (NetworkLifecycle.Instance != null)
            NetworkLifecycle.Instance.OnTick -= OnTick;

        if (station != null)
        {
            station.Registered -= OnStationRegistered;
            if (station.StationController != null && station.StationController.logicStation != null)
                pendingJobs.Remove(station.StationController.logicStation);
        }

        Destroy(this);
    }

    private void OnStationRegistered()
    {
        station.Registered -= OnStationRegistered;
        ready = true;
        DrainPendingJobs();
        ReconcileExistingJobs();
    }

    private void AddJob(Job job)
    {
        if (job == null)
            return;
        if (NetworkedJob.TryGetFromJob(job, out NetworkedJob existing))
        {
            station.NetworkedJobs.Add(existing);
            BindExistingJobOverview(existing);
            return;
        }

        NetworkedJob networkedJob = new GameObject($"NetworkedJob {job.ID}").AddComponent<NetworkedJob>();
        networkedJob.Initialize(job, station);
        station.NetworkedJobs.Add(networkedJob);

        newJobs.Add(networkedJob);

        //Setup handlers
        networkedJob.OnJobDirty += OnJobDirty;
        BindExistingJobOverview(networkedJob);

        DebugRuntime.Publish("job", "job.host-network-registration-complete", DebugRuntimeSide.Server,
            entityType: "Job", entityId: networkedJob.NetId.ToString(), data: new()
            {
                ["jobId"] = job.ID ?? string.Empty,
                ["stationId"] = station.StationController.logicStation?.ID ?? string.Empty,
                ["hasOverview"] = networkedJob.JobOverview != null
            });
    }

    private void DrainPendingJobs()
    {
        Station logicStation = station.StationController.logicStation;
        if (!pendingJobs.TryGetValue(logicStation, out HashSet<Job> jobs))
            return;
        pendingJobs.Remove(logicStation);
        foreach (Job job in jobs)
            AddJob(job);
    }

    // Jobs the station had before it was ready, such as the ones restored from the save.
    private void ReconcileExistingJobs()
    {
        HashSet<Job> jobs = [];
        AddJobs(station.AvailableJobs, jobs);
        AddJobs(station.TakenJobs, jobs);
        AddJobs(station.AbandonedJobs, jobs);
        AddJobs(station.CompletedJobs, jobs);
        foreach (Job job in jobs)
            AddJob(job);
    }

    private static void AddJobs(IEnumerable<Job> source, HashSet<Job> destination)
    {
        if (source == null)
            return;
        foreach (Job job in source)
            if (job != null)
                destination.Add(job);
    }

    private void BindExistingJobOverview(NetworkedJob networkedJob)
    {
        if (networkedJob?.Job == null || networkedJob.JobOverviewNetId != 0)
            return;
        JobOverview overview = station.StationController.spawnedJobOverviews?
            .LastOrDefault(candidate => candidate != null && ReferenceEquals(candidate.job, networkedJob.Job));
        if (overview == null)
            return;
        NetworkedItem item = overview.GetOrAddComponent<NetworkedItem>();
        item.Initialize(overview, item.NetId, false);
        networkedJob.JobOverview = item;
        DebugRuntime.Publish("job", "job.overview-late-bound", DebugRuntimeSide.Server,
            entityType: "Job", entityId: networkedJob.NetId.ToString(), data: new()
            {
                ["jobId"] = networkedJob.Job.ID ?? string.Empty,
                ["itemNetId"] = item.NetId,
                ["stationId"] = station.StationController.logicStation?.ID ?? string.Empty
            });
    }

    private void OnJobDirty(NetworkedJob job)
    {
        if (!dirtyJobs.Contains(job))
            dirtyJobs.Add(job);
    }

    private void OnTick(uint tick)
    {
        //Send new jobs
        if (newJobs.Count > 0)
        {
            NetworkServer.Instance.SendJobsCreatePacket(station, newJobs.ToArray());
            newJobs.Clear();
        }

        //Send jobs with a changed status
        // Cleared first: a job that fails to send mustn't hold up the station's updates for good.
        if (dirtyJobs.Count > 0)
        {
            NetworkedJob[] batch = dirtyJobs.ToArray();
            dirtyJobs.Clear();
            NetworkServer.Instance.SendJobsUpdatePacket(station.NetId, batch);
        }
    }
}
