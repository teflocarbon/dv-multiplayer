using DV.Booklets;
using DV.Logic.Job;
using DV.ThingTypes;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Jobs;

/// <summary>
/// A remote client's part of a station: builds its copies of the server's jobs from
/// ClientboundJobsCreatePacket and applies ClientboundJobsUpdatePacket, printing the paperwork
/// the server's validator printed. Runs its coroutines on the station's
/// <see cref="NetworkedStationController"/>.
/// </summary>
public static class ClientStationJobs
{
    const int MAX_FRAMES = 120;

    public static void AddJobs(NetworkedStationController station, JobData[] jobs)
    {

        foreach (JobData jobData in jobs)
        {
            //Cars may still be loading, we shouldn't spawn the job until they are ready
            if (CheckCarsLoaded(jobData))
            {
                Multiplayer.LogDebug(() => $"AddJobs() calling AddJob({jobData.ID})");
                AddJob(station, jobData);
            }
            else
            {
                Multiplayer.LogDebug(() => $"AddJobs() Delaying({jobData.ID})");
                station.StartCoroutine(DelayCreateJob(station, jobData));
            }
        }
    }

    private static void AddJob(NetworkedStationController station, JobData jobData)
    {
        var newJobData = JobData.ToJob(jobData);

        Job newJob = newJobData.newJob;
        var netIdToTask = newJobData.netIdToTask;

        var carNetIds = jobData.GetCars();

        NetworkedJob networkedJob = CreateNetworkedJob(station, newJob, jobData.NetID, carNetIds, netIdToTask);
        station.NetworkedJobs.Add(networkedJob);

        if (networkedJob.Job.State == JobState.Available)
        {
            station.StationController.logicStation.AddJobToStation(newJob);
            station.StationController.processedNewJobs.Add(newJob);

            if (jobData.ItemNetID != 0)
            {
                GenerateOverview(station, networkedJob, jobData.ItemNetID, jobData.ItemPosition);
            }
        }
        else if (networkedJob.Job.State == JobState.InProgress)
        {
            station.StationController.logicStation.AddJobToStation(newJob);
            station.StationController.processedNewJobs.Add(newJob);

            station.TakenJobs.Add(newJob);
            newJob.TakeJob(true); //take job as if loaded from save to prevent debt controller kicking in
            foreach (JobBookletCopyData booklet in jobData.JobBooklets ?? [])
                GenerateJobBookletCopy(networkedJob, booklet.ItemNetId, booklet.Position,
                    booklet.IssuedToPlayerId);
        }
        else
        {
            //we don't need to update anything, so we'll return
            //Maybe item sync will require knowledge of the job for expired/failed/completed reports, but we currently only sync these for connected players
            return;
        }


        Multiplayer.LogDebug(() => $"AddJob({jobData.ID}) Starting plate update {newJob.ID} count: {jobData.GetCars().Count}");
        station.StartCoroutine(UpdateCarPlates(carNetIds, newJob.ID));

        Multiplayer.Log($"Added NetworkedJob {newJob.ID} to NetworkedStationController {station.StationController.logicStation.ID}");
    }

    private static IEnumerator DelayCreateJob(NetworkedStationController station, JobData jobData)
    {
        int frameCounter = 0;

        Multiplayer.LogDebug(() => $"DelayCreateJob([{jobData.NetID}, {jobData.ID}]) job type: {jobData.JobType}");

        yield return new WaitForEndOfFrame();

        while (frameCounter < MAX_FRAMES)
        {
            if (CheckCarsLoaded(jobData))
            {
                Multiplayer.LogDebug(() => $"DelayCreateJob([{jobData.NetID}, {jobData.ID}]) job type: {jobData.JobType}. Successfully created cars!");
                AddJob(station, jobData);
                yield break;
            }

            frameCounter++;
            yield return new WaitForEndOfFrame();
        }

        Multiplayer.LogWarning($"Timeout waiting for cars to load for job [{jobData.NetID}, {jobData.ID}]");
    }

    private static bool CheckCarsLoaded(JobData jobData)
    {
        //extract all cars from the job and verify they have been initialised
        foreach (var carNetId in jobData.GetCars())
        {
            if (!NetworkedTrainCar.TryGet(carNetId, out NetworkedTrainCar car) || !car.Client_Initialized)
            {
                //car not spawned or not yet initialised
                return false;
            }
        }

        return true;
    }

    private static NetworkedJob CreateNetworkedJob(NetworkedStationController station, Job job, ushort netId, List<ushort> carNetIds, Dictionary<ushort, Task> netIdToTask)
    {
        NetworkedJob networkedJob = new GameObject($"NetworkedJob {job.ID}").AddComponent<NetworkedJob>();
        networkedJob.NetId = netId;
        networkedJob.Initialize(job, station);
        networkedJob.SetTasksFromServer(netIdToTask);
        networkedJob.JobCars = carNetIds;
        return networkedJob;
    }

    public static void UpdateJobs(NetworkedStationController station, JobUpdateStruct[] jobs)
    {
        foreach (JobUpdateStruct job in jobs)
        {
            if (!NetworkedJob.Get(job.JobNetID, out NetworkedJob netJob))
                continue;

            netJob.Job.startTime = job.StartTime;
            netJob.Job.finishTime = job.FinishTime;

            UpdateJobState(station, netJob, job);
            UpdateJobOverview(station, netJob, job);
            UpdateJobBooklet(netJob, job);

        }
    }

    private static void UpdateJobState(NetworkedStationController station, NetworkedJob netJob, JobUpdateStruct job)
    {
        if (netJob.Job.State != job.JobState)
        {
            netJob.Job.State = job.JobState;
            HandleJobStateChange(station, netJob, job);
        }
    }

    private static void UpdateJobOverview(NetworkedStationController station, NetworkedJob netJob, JobUpdateStruct job)
    {
        Multiplayer.Log($"UpdateJobOverview({netJob.Job.ID}) State: {job.JobState}, ItemNetId: {job.ItemNetID}");
        if (job.JobState == DV.ThingTypes.JobState.Available && job.ItemNetID != 0)
        {
            if (netJob.JobOverview == null)
                GenerateOverview(station, netJob, job.ItemNetID, job.ItemPositionData);
            /*
            else
                netJob.JobOverview.NetId = job.ItemNetID;
            */
        }
    }

    private static void UpdateJobBooklet(NetworkedJob netJob, JobUpdateStruct job)
    {
        if (job.JobState != JobState.InProgress || job.ItemNetID == 0 ||
            netJob.TryGetJobBooklet(job.ItemNetID, out _))
            return;
        GenerateJobBookletCopy(netJob, job.ItemNetID, job.ItemPositionData,
            job.IssuedToPlayerId);
    }

    private static void HandleJobStateChange(NetworkedStationController station, NetworkedJob netJob, JobUpdateStruct updateData)
    {
        JobValidator validator = null;
        NetworkedItem netItem;
        string jobIdStr = $"[{netJob?.Job?.ID}, {netJob.NetId}]";

        NetworkClient.Instance.LogDebug(() => $"HandleJobStateChange({jobIdStr}) Current state: {netJob?.Job?.State}, New state: {updateData.JobState}, ValidationStationNetId: {updateData.ValidationStationId}, ItemNetId: {updateData.ItemNetID}");

        // Job state packets only create the accepted-job booklet. Job reports are physical
        // print artifacts and arrive through ClientboundJobReportArtifactPacket.
        bool shouldPrint = updateData.JobState == JobState.InProgress;
        bool canPrint = true;

        if (shouldPrint)
        {
            if (updateData.ValidationStationId != 0 && NetworkedStationController.Get(updateData.ValidationStationId, out var netStation))
            {
                validator = netStation.JobValidator;
            }
            else
            {
                NetworkClient.Instance.LogError($"HandleJobStateChange({jobIdStr}) Validator not found or data missing! Validator ID: {updateData.ValidationStationId}");
                canPrint = false;
            }

            if (updateData.ItemNetID == 0)
            {
                NetworkClient.Instance.LogError($"HandleJobStateChange({jobIdStr}) Missing item data!");
                canPrint = false;
            }
        }


        bool printed = false;
        switch (netJob.Job.State)
        {
            case JobState.InProgress:
                station.AvailableJobs.Remove(netJob.Job);
                station.TakenJobs.Add(netJob.Job);

                netJob.Job.TakeJob(true); //take job as if loaded from save to prevent debt controller kicking in

                if (canPrint)
                {
                    JobBooklet jobBooklet = BookletCreator.CreateJobBooklet(netJob.Job, validator.bookletPrinter.spawnAnchor.position, validator.bookletPrinter.spawnAnchor.rotation, WorldMover.OriginShiftParent, true);
                    netItem = jobBooklet.GetOrAddComponent<NetworkedItem>();
                    netItem.Initialize(jobBooklet, updateData.ItemNetID, false);
                    netJob.RegisterJobBooklet(netItem, updateData.IssuedToPlayerId);
                    printed = true;
                }

                netJob.DestroyJobOverview();

                break;

            case JobState.Completed:
                station.TakenJobs.Remove(netJob.Job);
                station.CompletedJobs.Add(netJob.Job);
                netJob.Job.CompleteJob();

                station.StartCoroutine(UpdateCarPlates(netJob.JobCars, string.Empty));

                netJob.DestroyJobBooklets();

                break;

            case JobState.Abandoned:
                station.TakenJobs.Remove(netJob.Job);
                station.AbandonedJobs.Add(netJob.Job);
                netJob.Job.AbandonJob();
                station.StartCoroutine(UpdateCarPlates(netJob.JobCars, string.Empty));
                break;

            case JobState.Expired:
                netJob.Job.ExpireJob();
                netJob.DestroyJobOverview();

                station.StartCoroutine(UpdateCarPlates(netJob.JobCars, string.Empty));
                break;

            default:
                NetworkClient.Instance.LogError($"HandleJobStateChange({jobIdStr}) Unrecognised Job State: {updateData.JobState}");
                break;
        }

        if (printed)
        {
            netJob.ValidatorResponseReceived = true;
            netJob.ValidationAccepted = true;
            validator.jobValidatedSound.Play(validator.bookletPrinter.spawnAnchor.position, 1f, 1f, 0f, 1f, 500f, default, null, validator.transform, false, 0f, null);
            validator.bookletPrinter.Print(false);
        }
    }

    public static void RemoveJob(NetworkedStationController station, NetworkedJob job)
    {
        if (station.AvailableJobs.Contains(job.Job))
            station.AvailableJobs.Remove(job.Job);

        if (station.TakenJobs.Contains(job.Job))
            station.TakenJobs.Remove(job.Job);

        if (station.CompletedJobs.Contains(job.Job))
            station.CompletedJobs.Remove(job.Job);

        if (station.AbandonedJobs.Contains(job.Job))
            station.AbandonedJobs.Remove(job.Job);

        job.DestroyJobOverview();
        job.DestroyJobBooklets();

        job.ClearReports();

        station.NetworkedJobs.Remove(job);
        GameObject.Destroy(job);
    }

    public static IEnumerator UpdateCarPlates(List<ushort> carNetIds, string jobId)
    {

        Multiplayer.LogDebug(() => $"UpdateCarPlates({jobId}) carNetIds: {carNetIds?.Count}");

        if (carNetIds == null || string.IsNullOrEmpty(jobId))
            yield break;

        foreach (ushort carNetId in carNetIds)
        {
            int frameCounter = 0;
            TrainCar trainCar = null;

            while (frameCounter < MAX_FRAMES)
            {

                if (NetworkedTrainCar.TryGet(carNetId, out trainCar) &&
                    trainCar != null &&
                    trainCar.trainPlatesCtrl?.trainCarPlates != null &&
                    trainCar.trainPlatesCtrl.trainCarPlates.Count > 0)
                {
                    trainCar.UpdateJobIdOnCarPlates(jobId);
                    break;
                }

                Multiplayer.LogDebug(() => $"UpdateCarPlates({jobId}) car: {carNetId}, frameCount: {frameCounter}. Incrementing frames");
                frameCounter++;
                yield return new WaitForEndOfFrame();
            }

            if (frameCounter >= MAX_FRAMES)
            {
                Multiplayer.LogError($"Failed to update plates for car [{trainCar?.ID}, {carNetId}] (Job: {jobId}) after {frameCounter} frames");
            }
        }
    }

    private static void GenerateOverview(NetworkedStationController station, NetworkedJob networkedJob, ushort itemNetId, ItemPositionData posData)
    {
        Multiplayer.Log($"GenerateOverview([{networkedJob.Job.ID},{networkedJob.Job.jobType}], {itemNetId}) Position: {posData.Position}, Less currentMove: {posData.Position + WorldMover.currentMove}");
        JobOverview jobOverview = BookletCreator_JobOverview.Create(networkedJob.Job, posData.Position + WorldMover.currentMove, posData.Rotation, WorldMover.OriginShiftParent);

        NetworkedItem netItem = jobOverview.GetOrAddComponent<NetworkedItem>();
        netItem.Initialize(jobOverview, itemNetId, false);
        networkedJob.JobOverview = netItem;
        station.StationController.spawnedJobOverviews.Add(jobOverview);
        station.StartCoroutine(ObserveJobOverviewRender(networkedJob, netItem));
    }

    private static IEnumerator ObserveJobOverviewRender(NetworkedJob networkedJob, NetworkedItem item)
    {
        string jobId = networkedJob?.Job?.ID ?? string.Empty;
        ushort itemNetId = item?.NetId ?? (ushort)0;
        PageBook pageBook = item?.GetComponentInChildren<PageBook>(true);
        if (pageBook == null)
        {
            DebugRuntime.Publish("job", "job.overview-render-pagebook-missing",
                DebugRuntimeSide.Client, DebugSeverity.Error, "Job", networkedJob?.NetId.ToString(),
                new() { ["jobId"] = jobId, ["itemNetId"] = itemNetId });
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + 15f;
        while (item != null && !pageBook.PagesGenerated && Time.realtimeSinceStartup < deadline)
            yield return new WaitForEndOfFrame();

        bool ready = item != null && pageBook.PagesGenerated && pageBook.PageNum > 0;
        DebugRuntime.Publish("job", ready ? "job.overview-render-ready" : "job.overview-render-timeout",
            DebugRuntimeSide.Client, ready ? DebugSeverity.Info : DebugSeverity.Error,
            "Job", networkedJob?.NetId.ToString(), new()
            {
                ["jobId"] = jobId,
                ["itemNetId"] = itemNetId,
                ["representationAlive"] = item != null,
                ["pagesGenerated"] = item != null && pageBook.PagesGenerated,
                ["pageCount"] = item != null ? pageBook.PageNum : 0,
                ["runtimePageCount"] = item != null ? pageBook.pages?.Count ?? 0 : 0
            });
    }

    private static void GenerateJobBookletCopy(NetworkedJob networkedJob, ushort itemNetId,
        ItemPositionData posData, byte issuedToPlayerId)
    {
        if (networkedJob == null || itemNetId == 0 ||
            networkedJob.TryGetJobBooklet(itemNetId, out _))
            return;
        JobBooklet booklet = BookletCreator.CreateJobBooklet(networkedJob.Job,
            posData.Position + WorldMover.currentMove, posData.Rotation,
            WorldMover.OriginShiftParent, true);
        NetworkedItem item = booklet.GetOrAddComponent<NetworkedItem>();
        item.Initialize(booklet, itemNetId, false);
        networkedJob.RegisterJobBooklet(item, issuedToPlayerId);
    }
}
