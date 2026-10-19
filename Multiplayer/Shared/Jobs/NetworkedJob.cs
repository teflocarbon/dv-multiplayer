using DV.CabControls;
using DV.InventorySystem;
using DV.Logic.Job;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Session;
using Multiplayer.Core.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Jobs;

public class NetworkedJob : IdMonoBehaviour<ushort, NetworkedJob>
{
    #region Lookup Cache

    private static readonly Dictionary<Job, NetworkedJob> jobToNetworkedJob = [];
    private static readonly Dictionary<string, NetworkedJob> jobIdToNetworkedJob = [];
    private static readonly Dictionary<string, Job> jobIdToJob = [];

    public static bool Get(ushort netId, out NetworkedJob obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedJob> rawObj);
        obj = (NetworkedJob)rawObj;
        return b;
    }

    public static bool TryGetJob(ushort netId, out Job obj)
    {
        bool b = Get(netId, out NetworkedJob networkedJob);
        obj = b ? networkedJob.Job : null;
        return b;
    }

    public static bool TryGetFromJob(Job job, out NetworkedJob networkedJob)
    {
        return jobToNetworkedJob.TryGetValue(job, out networkedJob);
    }

    public static bool TryGetFromJobId(string jobId, out NetworkedJob networkedJob)
    {
        return jobIdToNetworkedJob.TryGetValue(jobId, out networkedJob);
    }

    public static bool TryGetNetId(Job job, out ushort netId)
    {
        if (TryGetFromJob(job, out var networkedJob))
        {
            netId = networkedJob.NetId;
            return true;
        }

        netId = 0;
        return false;
    }

    public static IReadOnlyCollection<NetworkedJob> GetAllJobs() =>
        jobToNetworkedJob.Values.Distinct().ToArray();

    #endregion

    private static readonly Dictionary<Job, List<Task>> pendingJobTasks = [];

    protected override bool IsIdServerAuthoritative => true;
    public enum DirtyCause
    {
        JobOverview,
        JobBooklet,
        JobState
    }

    public Job Job { get; private set; }
    public NetworkedStationController Station { get; private set; }

    private NetworkedItem _jobOverview;

    public NetworkedItem JobOverview
    {
        get => _jobOverview;
        set
        {
            if (value != null && value.GetTrackedItem<JobOverview>() == null)
                return;

            _jobOverview = value;

            if (value != null)
            {
                Cause = DirtyCause.JobOverview;
                OnJobDirty?.Invoke(this);
            }
        }
    }

    private readonly Dictionary<ushort, NetworkedItem> jobBooklets = [];
    private readonly JobBookletIssuanceIndex issuedBooklets = new();

    public IReadOnlyCollection<NetworkedItem> JobBooklets => jobBooklets.Values;
    public NetworkedItem LastChangedJobBooklet { get; private set; }
    public byte LastChangedJobBookletIssuedToPlayerId { get; private set; }
    public byte PendingBookletIssuedToPlayerId { get; set; }

    public bool RegisterJobBooklet(NetworkedItem item, byte issuedToPlayerId)
    {
        if (item == null || item.NetId == 0 || item.GetTrackedItem<JobBooklet>() == null)
            return false;
        jobBooklets[item.NetId] = item;
        if (issuedToPlayerId != 0)
            issuedBooklets.Assign(issuedToPlayerId, item.NetId);
        LastChangedJobBooklet = item;
        LastChangedJobBookletIssuedToPlayerId = issuedToPlayerId;
        Cause = DirtyCause.JobBooklet;
        OnJobDirty?.Invoke(this);
        return true;
    }

    public bool TryGetJobBooklet(ushort itemNetId, out NetworkedItem item) =>
        jobBooklets.TryGetValue(itemNetId, out item) && item != null;

    public bool TryGetIssuedJobBooklet(byte playerId, out NetworkedItem item)
    {
        item = null;
        return issuedBooklets.TryGetItem(playerId, out ushort itemNetId) &&
            TryGetJobBooklet(itemNetId, out item);
    }

    public bool TryGetIssuedPlayer(ushort itemNetId, out byte playerId)
    {
        return issuedBooklets.TryGetPlayer(itemNetId, out playerId);
    }

    public void UnregisterJobBooklet(NetworkedItem item)
    {
        if (item == null)
            return;
        jobBooklets.Remove(item.NetId);
        issuedBooklets.RemoveItem(item.NetId);
        if (ReferenceEquals(LastChangedJobBooklet, item))
            LastChangedJobBooklet = null;
    }
    private readonly List<NetworkedItem> JobReports = [];

    public Guid OwnedBy { get; set; } = Guid.Empty;
    public JobValidator JobValidator { get; set; }

    public bool ValidatorRequestSent { get; set; } = false;
    public bool ValidatorResponseReceived { get; set; } = false;
    public bool ValidationAccepted { get; set; } = false;
    public ValidationType ValidationType { get; set; }

    public DirtyCause Cause { get; private set; }

    public Action<NetworkedJob> OnJobDirty;

    public List<ushort> JobCars = [];

    protected override void Awake()
    {
        base.Awake();
    }

    protected void Start()
    {
        if (Job != null)
        {
            AddToCache();
        }
        else
        {
            Multiplayer.LogError($"NetworkedJob Start(): Job is null for {gameObject.name}");
        }
    }

    public void Initialize(Job job, NetworkedStationController station)
    {
        Job = job;
        Station = station;

        transform.SetParent(station.transform);

        // Setup handlers
        job.JobTaken += OnJobTaken;
        job.JobAbandoned += OnJobAbandoned;
        job.JobCompleted += OnJobCompleted;
        job.JobExpired += OnJobExpired;

        // If this is called after Start(), we need to add to cache here
        if (gameObject.activeInHierarchy)
        {
            AddToCache();
        }

        // Check for any pending tasks that were added before the NetworkedJob was created
        if (pendingJobTasks.TryGetValue(job, out var taskList) && taskList != null)
        {
            pendingJobTasks.Remove(job);

            //Multiplayer.LogDebug(() => $"NetworkedJob.Initialize(): Found {taskList.Count} pending tasks for jobId {job.ID}");

            foreach (var task in taskList)
                CreateNetworkedTask(task);
        }

        //Multiplayer.LogDebug(() => $"NetworkedJob.Initialize(): Initialized NetworkedJob for jobId {job.ID} with {Job.tasks.Count} tasks");
    }

    private void AddToCache()
    {
        jobToNetworkedJob[Job] = this;
        jobIdToNetworkedJob[Job.ID] = this;
        jobIdToJob[Job.ID] = Job;
        //Multiplayer.Log($"NetworkedJob added to cache: {Job.ID}");
    }

    public static void EnqueueTask(Task task, Job job)
    {
        if (TryGetFromJob(job, out var netJob))
        {
            Multiplayer.LogDebug(() => $"NetworkedJob.EnqueueTask(): Creating task immediately for jobId {task.Job.ID}");
            netJob.CreateNetworkedTask(task);
            return;
        }

        Multiplayer.LogDebug(() => $"NetworkedJob.EnqueueTask(): Enqueuing task for later creation for jobId {task.Job.ID}");

        if (!pendingJobTasks.TryGetValue(task.Job, out var taskList))
        {
            taskList = [];
            pendingJobTasks[task.Job] = taskList;
        }
        taskList.Add(task);
    }

    public void SetTasksFromServer(Dictionary<ushort, Task> netIdToTask)
    {
        if (netIdToTask == null)
        {
            Multiplayer.LogError($"NetworkedJob.SetTasksFromServer(): netIdToTask is null for jobId {Job?.ID}");
            return;
        }
        
        foreach (var kvp in netIdToTask)
        {
            CreateNetworkedTask(kvp.Value, kvp.Key);
        }
    }

    private void CreateNetworkedTask(Task task, ushort netId = 0)
    {
        if (task == null)
        {
            Multiplayer.LogError($"NetworkedJob.CreateNetworkedTask(): Task is null for jobId {Job?.ID}");
            return;
        }

        NetworkedTask taskObj = new GameObject().AddComponent<NetworkedTask>();
        taskObj.Initialize(task, netId);
        taskObj.name = $"{Job.ID}-{taskObj.NetId}";
        taskObj.transform.SetParent(transform);
    }

    private void OnJobTaken(Job job, bool viaLoadGame)
    {
        if (viaLoadGame)
            return;

        Cause = DirtyCause.JobState;
        OnJobDirty?.Invoke(this);
    }

    private void OnJobAbandoned(Job job)
    {
        Cause = DirtyCause.JobState;
        OnJobDirty?.Invoke(this);
        if (NetworkLifecycle.Instance?.IsHost() == true)
            DestroyJobBooklets();
    }

    private void OnJobCompleted(Job job)
    {
        Cause = DirtyCause.JobState;
        OnJobDirty?.Invoke(this);
        if (NetworkLifecycle.Instance?.IsHost() == true)
            DestroyJobBooklets();
    }

    private void OnJobExpired(Job job)
    {
        Cause = DirtyCause.JobState;
        OnJobDirty?.Invoke(this);
        if (NetworkLifecycle.Instance?.IsHost() == true)
            DestroyJobBooklets();
    }

    public void AddReport(NetworkedItem item)
    {
        if (item == null || !item.UsefulItem)
        {
            Multiplayer.LogError($"Attempted to add a null or uninitialised report: JobId: {Job?.ID}, JobNetID: {NetId}");
            return;
        }

        Type reportType = item.TrackedItemType;
        if (reportType == typeof(JobReport) ||
               reportType == typeof(JobExpiredReport) ||
               reportType == typeof(JobMissingLicenseReport) /*||
               reportType == typeof(Debtre) ||*/
           )
        {
            JobReports.Add(item);
        }
    }

    public void DestroyJobOverview()
    {
        JobOverview joItem = JobOverview?.GetTrackedItem<JobOverview>();

        if (joItem != null)
        {
            RemoveFromInventory(joItem.GetComponent<ItemBase>(), JobOverview.NetId);
            joItem.DestroyJobOverview();
            return;
        }

        // This side may not know which overview belongs to the job (e.g. it was handed over before
        // the job's item link arrived); any overview of this job still in the inventory is stale.
        if (Job == null || Inventory.Instance == null)
            return;
        foreach (GameObject held in Inventory.Instance.GetItemsArray(true) ?? [])
        {
            JobOverview stale = held != null ? held.GetComponent<JobOverview>() : null;
            if (stale == null || stale.job != Job)
                continue;
            RemoveFromInventory(stale.GetComponent<ItemBase>(), stale.GetComponent<NetworkedItem>()?.NetId ?? 0);
            stale.DestroyJobOverview();
        }
    }

    // When another player takes the job, this player's copy of its paperwork is destroyed. Vanilla's
    // drop leaves a locked or reserved slot pointing at the item, so the slot kept a dead overview
    // that couldn't be taken out until the job's booklet was printed. Clear every trace of it.
    private static void RemoveFromInventory(ItemBase itemBase, ushort netId)
    {
        if (itemBase == null)
            return;
        itemBase.ForceEndInteraction();
        itemBase.InContainer?.RemoveItem(itemBase.gameObject, false, true);
        InventoryIntegration.RevokeMembership(itemBase.gameObject, netId);
        InventoryIntegration.PurgeContainerMembership(itemBase.gameObject);
    }

    public void DestroyJobBooklets()
    {
        foreach (NetworkedItem item in jobBooklets.Values.ToArray())
        {
            JobBooklet jbItem = item?.GetTrackedItem<JobBooklet>();
            if (jbItem == null)
                continue;
            RemoveFromInventory(jbItem.GetComponent<ItemBase>(), item.NetId);
            jbItem.DestroyJobBooklet();
        }
        jobBooklets.Clear();
        issuedBooklets.Clear();
        LastChangedJobBooklet = null;
    }

    public void RemoveReport(NetworkedItem item)
    {

    }

    public void ClearReports()
    {
        foreach (var report in JobReports)
            Destroy(report.gameObject);

        JobReports.Clear();
    }

    protected void OnDisable()
    {
        if (UnloadWatcher.isQuitting || UnloadWatcher.isUnloading)
            return;

        // Remove from lookup caches
        jobToNetworkedJob.Remove(Job);
        jobIdToNetworkedJob.Remove(Job.ID);
        jobIdToJob.Remove(Job.ID);

        // Unsubscribe from events
        Job.JobTaken -= OnJobTaken;
        Job.JobAbandoned -= OnJobAbandoned;
        Job.JobCompleted -= OnJobCompleted;
        Job.JobExpired -= OnJobExpired;

        Destroy(this);
    }

}
