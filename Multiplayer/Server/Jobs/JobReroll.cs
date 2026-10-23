using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Trains;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Server.Trains;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
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
/// If nobody is in or on the job's cars, the server expires the job, deletes all its cars at once
/// (vanilla would leave them for the unused-car deleter), even ones coupled to a locomotive, and
/// lets the station generate a replacement. Otherwise it throws the paper back out of the bin at
/// the player who binned it. Every peer sees the result through the usual job, car and item sync.
/// </summary>
public static class JobReroll
{
    // How far the bin the client named may be from one this game has.
    private const float BinMatchDistance = 3f;
    // The paper flies out from this far above the top of the bin's trigger, so it doesn't touch it.
    private const float ThrowClearance = 0.3f;
    // The paper's speed out of the bin (m/s): about 2 m out and half a metre up.
    private const float ThrowOutwards = 2.5f;
    private const float ThrowUpwards = 3.5f;
    // GrabHandlerItem.Throw adds direction * mass * 140 as a force for one physics step.
    private const float ThrowForcePerDirection = 140f;
    // One throw at a time: every collider of the paper and every bounce asks again.
    private const float ThrowInterval = 1f;
    // How long to wait for the paper's drop to arrive before throwing it out of a hand anyway.
    private const float ReleaseWait = 1.5f;

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
        string refusal = Refusal(cars);
        if (refusal != null)
        {
            Publish("job.reroll-refused", networkedJob, requester, refusal);
            Multiplayer.Log($"{requester.Username} binned {networkedJob.Job.ID}, not re-rolled: {refusal}");
            CoroutineManager.Instance.StartCoroutine(ThrowBackOnceReleased(overview, bin, requester, refusal));
            return;
        }

        Reroll(networkedJob, cars, requester);
    }

    // Why the job can't be re-rolled, or null if it can: only a player in or on one of its cars
    // stops it (a car coupled to a locomotive is deleted all the same).
    private static string Refusal(List<TrainCar> cars)
    {
        foreach (TrainCar car in cars)
            if (IsOccupied(car))
                return $"someone is on {car.ID}";
        return null;
    }

    private static bool IsOccupied(TrainCar car)
    {
        if (!NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked))
            return false;
        if (networked.HasPlayers)
            return true;
        return NetworkServer.Instance.ServerPlayers.Any(player => player.CarId == networked.NetId);
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
    /// one the players are licensed for (vanilla's forcePlayerLicensedJobGeneration). A station
    /// nobody is near (the paper was carried to another station's bin) is left: it fills itself
    /// when a player arrives, rather than spawning cars nobody is there for.
    /// </summary>
    public static void GenerateReplacement(StationController station)
    {
        if (station.playerEnteredJobGenerationZone)
            station.ProceduralJobsController.TryToGenerateJobs();
    }

    private static IEnumerator ThrowBackOnceReleased(NetworkedItem item, JobAbandoner bin, ServerPlayer requester,
        string refusal)
    {
        // A player's game holds a gentle drop back for a moment (ItemReleaseGrace), and the paper
        // can be in the bin before its drop reaches the server. Thrown out first, the late drop was
        // refused and its correction put the paper back where the throw started, at rest.
        for (float t0 = Time.realtimeSinceStartup;
             item != null && Time.realtimeSinceStartup - t0 < ReleaseWait &&
             LiveItems.TryGet(item.NetId, out LiveItems.LiveItem held) &&
             held.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory;)
            yield return null;
        if (item == null || bin == null)
            yield break;
        float now = Time.realtimeSinceStartup;
        if (lastThrowAt.TryGetValue(item.NetId, out float last) && now - last < ThrowInterval)
            yield break;
        lastThrowAt[item.NetId] = now;

        Collider trigger = bin.GetComponent<Collider>();
        Bounds bounds = trigger != null ? trigger.bounds : new Bounds(bin.transform.position, Vector3.zero);
        Vector3 start = bounds.center + Vector3.up * (bounds.extents.y + ThrowClearance);
        Vector3 towardsPlayer = requester.WorldPosition - start;
        towardsPlayer.y = 0f;
        Vector3 outwards = towardsPlayer.sqrMagnitude > 0.01f ? towardsPlayer.normalized : bin.transform.forward;
        Vector3 velocity = outwards * ThrowOutwards + Vector3.up * ThrowUpwards;

        if (!ThrowOutOfBin(item, requester, start, velocity, refusal) ||
            requester != NetworkServer.Instance.LocalPlayer)
            yield break;

        // The host's own player simulates the flight here. Granting that lease ends the one its drop
        // had, which stops the body (RestoreLocalBodyAfterLease) and loses the throw's force: give
        // the paper its speed once the new lease is there. A client's game keeps the force.
        uint revision = item.AuthorityRevision;
        for (int frame = 0; frame < 10 && item != null; frame++)
        {
            yield return null;
            if (item == null || NetworkedItemManager.Instance?.Spatial?.TryGetActiveLeaseIdentity(item.NetId,
                    out uint leaseRevision, out _, out byte simulator, out _) != true ||
                simulator != requester.PlayerId || leaseRevision < revision)
                continue;
            Rigidbody body = item.Item?.ItemRigidbody;
            if (body != null && !body.isKinematic)
            {
                body.velocity = velocity;
                body.WakeUp();
            }
            yield break;
        }
    }

    // The server throws the paper from just above the bin. The player who binned it is the
    // transition's actor, so their game gets its physics lease and flies it.
    private static bool ThrowOutOfBin(NetworkedItem item, ServerPlayer requester, Vector3 start, Vector3 velocity,
        string refusal)
    {
        ItemUpdateData snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
        if (snapshot == null)
            return false;
        snapshot.ItemState = ItemState.Thrown;
        snapshot.PlayerId = 0;
        snapshot.ItemPosition = start - WorldMover.currentMove;
        // GrabHandlerItem.Throw applies this as a force for one physics step.
        snapshot.ThrowDirection = velocity / (ThrowForcePerDirection * Time.fixedDeltaTime);
        snapshot.WorldParentKind = ItemWorldParentKind.World;
        snapshot.WorldParentNetId = 0;
        snapshot.WorldParentKey = string.Empty;
        snapshot.InventoryClaimPlayerId = 0;
        snapshot.InventoryClaimSlot = -1;
        snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
        if (!LiveItems.TryApplyTransition(item, snapshot, requester,
                ItemTransitionReason.JobRerollRefused, true, out string rejection,
                cause: ItemChangeLog.Move(ItemMoveReason.ThrownOutOfBin, requester, refusal)) ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
        {
            Multiplayer.LogWarning($"Couldn't throw job overview {item.NetId} out of the bin: {rejection}");
            return false;
        }
        LiveItems.WriteToSnapshot(record, snapshot, ItemTransitionReason.JobRerollRefused);
        snapshot.ItemState = ItemState.Thrown;
        item.ApplyServerCanonicalSnapshot(snapshot);
        // Index it in its world cell now. A paper let go on a car was recorded in the car's
        // interior; moved into the world, it was in no cell until its flight was sampled, so every
        // player's interest dropped it and took it up again created at rest: the throw was lost.
        ServerItemManager.Instance?.RegisterHostWorldItem(item);
        NetworkServer.Instance.SendItemUpdatePacket(snapshot);
        return true;
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
