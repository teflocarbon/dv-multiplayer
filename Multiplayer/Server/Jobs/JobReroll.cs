using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using Multiplayer.Core.Items;
using Multiplayer.Core.Trains;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Server.Trains;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using UnityEngine;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// An offered job's overview dropped in a station office bin (Client/Jobs/JobAbandonerClientPatch).
/// If nobody is in or on the job's cars and none is coupled to a locomotive, the server expires
/// the job, deletes its cars at once (vanilla would leave them for the unused-car deleter) and
/// lets the station generate a replacement. Otherwise it throws the paper back out of the bin at
/// the player who binned it. Every peer sees the result through the usual job, car and item sync.
/// </summary>
public static class JobReroll
{
    // How far the bin the client named may be from one this game has.
    private const float BinMatchDistance = 3f;
    // The paper flies out from this far above the top of the bin's trigger, so it doesn't touch it.
    private const float ThrowClearance = 0.3f;
    // GrabHandlerItem.Throw gives the paper 2.8 m/s per unit of direction.
    private const float ThrowOutwards = 0.9f;
    private const float ThrowUpwards = 1.2f;
    // One throw at a time: every collider of the paper and every bounce asks again.
    private const float ThrowInterval = 1f;

    private static readonly Dictionary<ushort, float> lastThrowAt = [];

    public static void Handle(ServerboundJobRerollRequestPacket packet, ServerPlayer requester)
    {
        // A job taken, expired or re-rolled already: its paper goes with it, nothing to answer.
        if (requester == null || !NetworkedJob.Get(packet.JobNetId, out NetworkedJob networkedJob) ||
            networkedJob.Job == null || networkedJob.Job.State != JobState.Available)
            return;
        NetworkedItem overview = networkedJob.JobOverview;
        if (overview == null || overview.NetId != packet.ItemNetId)
        {
            Multiplayer.LogWarning($"Re-roll of {networkedJob.Job.ID}: item {packet.ItemNetId} is not its overview");
            return;
        }

        JobAbandoner bin = FindBin(packet.BinPosition);
        if (bin == null)
        {
            Multiplayer.LogWarning($"Re-roll of {networkedJob.Job.ID}: no bin at {packet.BinPosition}");
            return;
        }

        List<TrainCar> cars = JobCars(networkedJob.Job);
        string refusal = Refusal(networkedJob, bin, cars);
        if (refusal != null)
        {
            Publish("job.reroll-refused", networkedJob, requester, refusal);
            Multiplayer.Log($"{requester.Username} binned {networkedJob.Job.ID}, not re-rolled: {refusal}");
            ThrowBack(overview, bin, requester, refusal);
            return;
        }

        Reroll(networkedJob, cars, requester);
    }

    // Why the job can't be re-rolled from this bin, or null if it can.
    private static string Refusal(NetworkedJob networkedJob, JobAbandoner bin, List<TrainCar> cars)
    {
        if (NearestStation(bin.transform.position) != networkedJob.Station)
            return "the bin is at another station";
        foreach (TrainCar car in cars)
        {
            if (IsOccupied(car))
                return $"someone is on {car.ID}";
            if (car.trainset?.cars.Any(c => c != null && CarTypes.IsAnyLocomotiveOrTender(c.carLivery)) == true)
                return $"{car.ID} is coupled to a locomotive";
        }
        return null;
    }

    private static bool IsOccupied(TrainCar car)
    {
        if (!NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked))
            return false;
        if (networked.HasPlayers)
            return true;
        return NetworkLifecycle.Instance.Server.ServerPlayers.Any(player => player.CarId == networked.NetId);
    }

    private static void Reroll(NetworkedJob networkedJob, List<TrainCar> cars, ServerPlayer requester)
    {
        Job job = networkedJob.Job;
        StationController station = networkedJob.Station.StationController;
        Publish("job.reroll-accepted", networkedJob, requester, null, cars.Count);
        Multiplayer.Log($"{requester.Username} binned {job.ID}: re-rolling it, deleting {cars.Count} cars");

        JobOverview paper = networkedJob.JobOverview?.GetTrackedItem<JobOverview>();
        if (paper != null)
            station.spawnedJobOverviews.Remove(paper);
        networkedJob.DestroyJobOverview();

        // The chain marks the cars for the unused-car deleter, which waits until no player is
        // near: delete them now, so their track space is free for the replacement.
        job.ExpireJob();
        using (TrainChangeCauses.Scope(PlayerChangeCauses.Cause(CarRemovalReason.JobRerolled, requester, job.ID)))
            CarSpawner.Instance.DeleteTrainCars(cars, forceInstantDestroy: true);
        UnusedTrainCarDeleter.Instance.ClearInvalidCarReferencesAfterManualDelete();

        GenerateReplacement(station);
    }

    /// <summary>
    /// Refills the station's offered jobs, as entering its zone does. The first job generated is
    /// one the players are licensed for (vanilla's forcePlayerLicensedJobGeneration).
    /// </summary>
    public static void GenerateReplacement(StationController station) =>
        station.ProceduralJobsController.TryToGenerateJobs();

    private static void ThrowBack(NetworkedItem item, JobAbandoner bin, ServerPlayer requester, string refusal)
    {
        float now = Time.realtimeSinceStartup;
        if (lastThrowAt.TryGetValue(item.NetId, out float last) && now - last < ThrowInterval)
            return;
        lastThrowAt[item.NetId] = now;

        Collider trigger = bin.GetComponent<Collider>();
        Bounds bounds = trigger != null ? trigger.bounds : new Bounds(bin.transform.position, Vector3.zero);
        Vector3 start = bounds.center + Vector3.up * (bounds.extents.y + ThrowClearance);
        Vector3 towardsPlayer = requester.WorldPosition - start;
        towardsPlayer.y = 0f;
        Vector3 outwards = towardsPlayer.sqrMagnitude > 0.01f ? towardsPlayer.normalized : bin.transform.forward;

        ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
        if (snapshot == null)
            return;
        snapshot.ItemState = ItemState.Thrown;
        snapshot.PlayerId = 0;
        snapshot.ItemPosition = start - WorldMover.currentMove;
        snapshot.ThrowDirection = outwards * ThrowOutwards + Vector3.up * ThrowUpwards;
        snapshot.WorldParentKind = ItemWorldParentKind.World;
        snapshot.WorldParentNetId = 0;
        snapshot.WorldParentKey = string.Empty;
        snapshot.InventoryClaimPlayerId = 0;
        snapshot.InventoryClaimSlot = -1;
        snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
        // The player who binned it simulates the flight (the lease goes to the transition's actor).
        if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, requester,
                ItemTransitionReason.JobRerollRefused, true, out string rejection,
                cause: ItemChangeLog.Move(ItemMoveReason.ThrownOutOfBin, requester, refusal)) ||
            !AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
        {
            Multiplayer.LogWarning($"Couldn't throw job overview {item.NetId} out of the bin: {rejection}");
            return;
        }
        AuthoritativeItemRegistry.WriteToSnapshot(record, snapshot, ItemTransitionReason.JobRerollRefused);
        snapshot.ItemState = ItemState.Thrown;
        item.ApplyServerCanonicalSnapshot(snapshot);
        NetworkLifecycle.Instance.Server.SendItemUpdatePacket(snapshot);
    }

    private static List<TrainCar> JobCars(Job job)
    {
        if (!JobsManager.Instance.jobToJobCars.TryGetValue(job, out HashSet<Car> cars) || cars == null)
            return [];
        Dictionary<Car, TrainCar> trainCars = TrainCarRegistry.Instance.logicCarToTrainCar;
        return cars.Select(car => car != null && trainCars.TryGetValue(car, out TrainCar trainCar) ? trainCar : null)
            .Where(trainCar => trainCar != null).ToList();
    }

    private static JobAbandoner FindBin(Vector3 absolutePosition)
    {
        Vector3 position = absolutePosition + WorldMover.currentMove;
        return Object.FindObjectsOfType<JobAbandoner>()
            .Where(bin => Vector3.Distance(bin.transform.position, position) < BinMatchDistance)
            .OrderBy(bin => Vector3.Distance(bin.transform.position, position))
            .FirstOrDefault();
    }

    // The station whose office the bin is in: the one with the nearest validator.
    private static NetworkedStationController NearestStation(Vector3 position)
    {
        NetworkedStationController nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (JobValidator validator in Object.FindObjectsOfType<JobValidator>())
        {
            float distance = Vector3.Distance(validator.transform.position, position);
            if (distance < nearestDistance &&
                NetworkedStationController.GetFromJobValidator(validator, out NetworkedStationController station))
            {
                nearest = station;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    private static void Publish(string eventName, NetworkedJob job, ServerPlayer requester, string refusal,
        int carCount = 0)
    {
        DebugRuntime.Publish("job", eventName, DebugRuntimeSide.Server,
            refusal == null ? DebugSeverity.Info : DebugSeverity.Warning,
            "Job", job.NetId.ToString(), new()
            {
                ["jobId"] = job.Job?.ID ?? string.Empty,
                ["stationId"] = job.Station?.StationController?.logicStation?.ID ?? string.Empty,
                ["requestingPlayerId"] = requester.PlayerId,
                ["reason"] = refusal ?? string.Empty,
                ["carCount"] = carCount
            });
    }
}
