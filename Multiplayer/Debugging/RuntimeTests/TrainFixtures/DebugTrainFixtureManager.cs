#if DEBUG
using DV;
using DV.Damage;
using DV.Simulation.Controllers;
using DV.ThingTypes;
using Multiplayer.Components;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests.TrainFixtures;

internal sealed class DebugTrainFixtureManager
{
    public static DebugTrainFixtureManager Current { get; private set; }

    private readonly Dictionary<string, DebugTrainFixtureRecord> fixtures =
        new(StringComparer.Ordinal);
    private static readonly HashSet<TrainCar> protectedCars = new();
    private static readonly HashSet<Junction> leasedJunctions = new();
    private static bool applyingRouteLease;
    private static bool applyingAtomicRelocation;

    public DebugTrainFixtureManager() => Current = this;

    internal bool TryGetFixture(string fixtureId, out DebugTrainFixtureRecord fixture) =>
        fixtures.TryGetValue(fixtureId, out fixture);

    public static bool IsProtected(TrainCar car) => car != null && protectedCars.Contains(car);
    public static bool IsProtected(Bogie bogie) => bogie != null && IsProtected(bogie.Car);
    public static bool ShouldBlockJunctionMutation(Junction junction) =>
        junction != null && leasedJunctions.Contains(junction) && !applyingRouteLease;
    public static bool ShouldSuppressIndividualMovePacket(TrainCar car) =>
        applyingAtomicRelocation && car != null && car.GetComponent<DebugTrainFixtureTag>() != null;

    public IEnumerator Create(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        string fixtureId = Required(command, "fixtureId");
        if (fixtures.TryGetValue(fixtureId, out DebugTrainFixtureRecord existing) &&
            existing.State != DebugTrainFixtureState.Clean)
            throw new InvalidOperationException("fixture-already-exists:" + fixtureId);

        string locationId = Optional(command, "locationId", string.Empty);
        DebugTrainFixtureLocationRecord location = string.IsNullOrWhiteSpace(locationId) ? null :
            DebugTrainFixtureLocationCatalog.Resolve(locationId);
        string trackId = location?.TrackId ?? Required(command, "trackId");
        bool allowGeneric = location?.AllowGenericTrack ?? OptionalBool(command, "allowGenericTrack", false);
        string geometryHash = location?.GeometryHash ?? Optional(command, "geometryHash", string.Empty);
        DebugTrainTrackRecord track = null;
        bool loadLocation = false;
        try { track = DebugTrainTrackCatalog.Resolve(trackId, allowGeneric, geometryHash); }
        catch (InvalidOperationException exception) when (location != null &&
            (exception.Message.StartsWith("track-not-found:", StringComparison.Ordinal) ||
             exception.Message.StartsWith("track-network-id-unavailable:", StringComparison.Ordinal)))
        {
            loadLocation = true;
        }
        if (loadLocation)
        {
            IEnumerator load = DebugTrainFixtureLocationCatalog.TeleportAndWaitForTrack(location);
            while (load.MoveNext()) yield return load.Current;
            track = DebugTrainTrackCatalog.Resolve(trackId, allowGeneric, geometryHash);
        }
        int pointIndex = location?.PointIndex ?? RequiredInt(command, "pointIndex");
        var points = track.Track.GetKinkedPointSet()?.points ??
            throw new InvalidOperationException("track-point-set-unavailable:" + trackId);
        if (pointIndex < 0 || pointIndex >= points.Length)
            throw new InvalidOperationException("track-point-index-out-of-range:" + pointIndex);

        string[] liveries = Required(command, "liveries").Split(',')
            .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        if (liveries.Length == 0) throw new InvalidOperationException("fixture-has-no-cars");
        string[] roles = Optional(command, "roles", string.Empty).Split(',')
            .Select(value => value.Trim()).ToArray();
        bool withTrack = location?.WithTrackDirection ?? OptionalBool(command, "withTrackDirection", true);
        DebugTrainFixtureRecord fixture = new()
        {
            FixtureId = fixtureId,
            RunId = command.RunId,
            CreatedUtc = DateTime.UtcNow,
            State = DebugTrainFixtureState.Allocated,
            Track = track,
            LocationId = location?.Id ?? string.Empty,
            PointIndex = pointIndex,
            WithTrackDirection = withTrack,
            PreventDerailment = OptionalBool(command, "preventDerailment", true),
            ProtectFuses = OptionalBool(command, "protectFuses", true),
            SustainEngine = OptionalBool(command, "sustainEngine", true),
            PreventDamage = OptionalBool(command, "preventDamage", true),
            MaximumSpeedKph = Mathf.Clamp(OptionalFloat(command, "maximumSpeedKph", 45f), 1f, 200f),
            ControllerState = DebugTrainSpeedControllerState.Idle
        };
        fixtures[fixtureId] = fixture; // Ledger first: cleanup can now find every subsequent car.
        Publish(fixture, "train.fixture.allocated");

        Exception failure = null;
        IEnumerator create = CreateCarsAndFinalize(fixture, run, liveries, roles, pointIndex,
            withTrack);
        for (;;)
        {
            bool moved;
            object current = null;
            try
            {
                moved = create.MoveNext();
                if (moved) current = create.Current;
            }
            catch (Exception exception)
            {
                moved = false;
                failure = exception;
            }
            if (!moved) break;
            yield return current;
        }
        if (failure == null) yield break;

        fixture.State = DebugTrainFixtureState.Failed;
        fixture.Failure = failure.GetBaseException().Message;
        Publish(fixture, "train.fixture.failed", severity: DebugSeverity.Error);
        IEnumerator cleanup = CleanupRecord(fixture);
        while (cleanup.MoveNext()) yield return cleanup.Current;
        throw failure;
    }

    private static IEnumerator CreateCarsAndFinalize(DebugTrainFixtureRecord fixture,
        RuntimeTestRunDto run, string[] liveries, string[] roles, int pointIndex, bool withTrack)
    {
            var points = fixture.Track.Track.GetKinkedPointSet().points;
            fixture.State = DebugTrainFixtureState.Spawning;
            int cursor = pointIndex;
            float previousHalfLength = 0f;
            for (int index = 0; index < liveries.Length; index++)
            {
                if (!TrainComponentLookup.Instance.LiveryFromId(liveries[index], out TrainCarLivery livery) ||
                    livery?.prefab == null)
                    throw new InvalidOperationException("unknown-train-livery:" + liveries[index]);
                Bounds bounds = CarSpawner.GetBoundsOfCar(livery.prefab);
                float halfLength = Mathf.Max(2f, bounds.extents.z);
                if (index > 0)
                {
                    float spacing = previousHalfLength + halfLength + 1f;
                    cursor += (withTrack ? -1 : 1) * Mathf.CeilToInt(spacing /
                        Mathf.Max(0.1f, (float)points[Mathf.Clamp(cursor, 0, points.Length - 1)].spanToNextPoint));
                }
                if (cursor < 0 || cursor >= points.Length)
                    throw new InvalidOperationException("fixture-consist-exceeds-track:" + index);
                var spawnPoint = points[cursor];
                if (!CarSpawner.IsThereSpaceForCarOnPoint(spawnPoint,
                        (Vector3)points[0].position, (Vector3)points[points.Length - 1].position,
                        bounds.extents))
                    throw new InvalidOperationException("fixture-track-space-unavailable:" + index);

                Vector3 forward = withTrack ? spawnPoint.forward : -spawnPoint.forward;
                TrainCar car = CarSpawner.Instance.SpawnCarFromRemote(livery.prefab, fixture.Track.Track,
                    (Vector3)spawnPoint.position, forward);
                if (car == null) throw new InvalidOperationException("fixture-car-spawn-failed:" + index);
                string role = index < roles.Length && roles[index].Length > 0 ? roles[index] :
                    index == 0 ? "Locomotive" : "Car" + index;
                DebugTrainFixtureTag tag = car.gameObject.GetOrAddComponent<DebugTrainFixtureTag>();
                tag.FixtureId = fixture.FixtureId;
                tag.RunId = fixture.RunId;
                tag.CarIndex = index;
                tag.Role = role;
                DebugTrainFixtureCarRecord carRecord = new()
                {
                    Index = index,
                    Role = role,
                    LiveryId = livery.id,
                    Car = car,
                    NetId = car.GetNetId(),
                    CarId = car.ID,
                    CarGuid = car.CarGUID
                };
                fixture.Cars.Add(carRecord);
                if (fixture.PreventDerailment) protectedCars.Add(car);
                ApplySafeStationaryState(car);
                Publish(fixture, "train.fixture.car-created", carRecord);
                previousHalfLength = halfLength;
                yield return null;
            }

            for (int index = 0; index + 1 < fixture.Cars.Count; index++)
            {
                TrainCar first = fixture.Cars[index].Car;
                TrainCar second = fixture.Cars[index + 1].Car;
                Coupler firstCoupler = withTrack ? first.rearCoupler : first.frontCoupler;
                Coupler secondCoupler = withTrack ? second.frontCoupler : second.rearCoupler;
                NetworkedCarSpawner.SetCouplingState(firstCoupler, secondCoupler,
                    ChainCouplerInteraction.State.Attached_Tight);
            }

            float timeout = Time.realtimeSinceStartup + 10f;
            while (fixture.Cars.Any(car => car.Car == null || car.Car.GetNetId() == 0 ||
                       !IsNetworkReady(car.Car)) && Time.realtimeSinceStartup < timeout)
            {
                foreach (DebugTrainFixtureCarRecord car in fixture.Cars)
                    if (car.Car != null) car.NetId = car.Car.GetNetId();
                yield return null;
            }
            if (fixture.Cars.Any(car => car.Car == null || car.Car.GetNetId() == 0))
                throw new InvalidOperationException("fixture-car-network-initialization-timeout");

            fixture.State = DebugTrainFixtureState.Ready;
            Publish(fixture, "train.fixture.ready");
            lock (run) run.Result["fixture"] = Snapshot(fixture);
    }

    public IEnumerator Observe(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        string fixtureId = Required(command, "fixtureId");
        ushort[] netIds = Required(command, "netIds").Split(',')
            .Select(value => ushort.TryParse(value.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out ushort id) ? id : (ushort)0)
            .Where(id => id != 0).ToArray();
        if (netIds.Length == 0) throw new InvalidOperationException("fixture-observe-has-no-netids");
        float deadline = Time.realtimeSinceStartup + 15f;
        List<TrainCar> cars = new();
        while (Time.realtimeSinceStartup <= deadline)
        {
            cars.Clear();
            bool complete = true;
            foreach (ushort netId in netIds)
            {
                if (!NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar networked) ||
                    networked?.TrainCar == null || !networked.Client_Initialized)
                { complete = false; break; }
                cars.Add(networked.TrainCar);
            }
            if (complete) break;
            yield return null;
        }
        if (cars.Count != netIds.Length) throw new InvalidOperationException("fixture-projection-timeout:" + fixtureId);
        lock (run)
        {
            run.Result["fixtureId"] = fixtureId;
            run.Result["netIds"] = netIds;
            run.Result["cars"] = cars.Select((car, index) => new Dictionary<string, object>
            {
                ["index"] = index,
                ["netId"] = netIds[index],
                ["carId"] = car.ID,
                ["carGuid"] = car.CarGUID,
                ["liveryId"] = car.carLivery?.id ?? string.Empty,
                ["trackNetIds"] = car.Bogies.Select(bogie => bogie.track != null &&
                    NetworkedRailTrack.TryGetNetId(bogie.track, out ushort id) ? id : (ushort)0).ToArray()
            }).ToArray();
        }
        DebugRuntime.Publish("train-fixture", "train.fixture.observed", DebugRuntimeSide.Shared,
            correlationId: command.RunId, data: new() { ["fixtureId"] = fixtureId,
                ["netIds"] = string.Join(",", netIds) });
    }

    public IEnumerator Status(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        yield return null;
        string fixtureId = Optional(command, "fixtureId", string.Empty);
        lock (run)
        {
            run.Result["fixtures"] = fixtures.Values
                .Where(fixture => fixtureId.Length == 0 || fixture.FixtureId == fixtureId)
                .Select(Snapshot).ToArray();
        }
    }

    public IEnumerator Catalog(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        yield return null;
        string contains = Optional(command, "contains", string.Empty);
        int limit = Mathf.Clamp(RequiredOrDefaultInt(command, "limit", 500), 1, 5000);
        var liveries = Globals.G?.Types?.Liveries?.Where(livery => livery?.prefab != null) ??
            Enumerable.Empty<TrainCarLivery>();
        if (!string.IsNullOrWhiteSpace(contains)) liveries = liveries.Where(livery =>
            livery.id.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0 ||
            livery.prefab.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
        lock (run)
        {
            run.Result["liveries"] = liveries.OrderBy(livery => livery.id,
                StringComparer.Ordinal).Take(limit).Select(livery =>
            {
                Bounds bounds = CarSpawner.GetBoundsOfCar(livery.prefab);
                return new Dictionary<string, object>
                {
                    ["liveryId"] = livery.id,
                    ["prefab"] = livery.prefab.name,
                    ["size"] = DebugValueSnapshotter.Snapshot(bounds.size),
                    ["hasSimulation"] = livery.prefab.GetComponentInChildren<DV.Simulation.Cars.SimController>(true) != null,
                    ["hasInterior"] = livery.prefab.GetComponentInChildren<TrainCar>(true)?.interior != null
                };
            }).ToArray();
        }
    }

    public IEnumerator Cleanup(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        string fixtureId = Required(command, "fixtureId");
        if (!fixtures.TryGetValue(fixtureId, out DebugTrainFixtureRecord fixture))
        {
            if (!OptionalBool(command, "ignoreMissing", false))
                throw new InvalidOperationException("fixture-not-found:" + fixtureId);
            lock (run)
            {
                run.Result["fixtureId"] = fixtureId;
                run.Result["alreadyAbsent"] = true;
                run.Result["clean"] = true;
            }
            yield break;
        }
        IEnumerator cleanup = CleanupRecord(fixture);
        while (cleanup.MoveNext()) yield return cleanup.Current;
        lock (run) run.Result["fixture"] = Snapshot(fixture);
    }

    public IEnumerator WaitForMotion(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        string fixtureId = Required(command, "fixtureId");
        if (!fixtures.TryGetValue(fixtureId, out DebugTrainFixtureRecord fixture))
            throw new InvalidOperationException("fixture-not-found:" + fixtureId);
        float minimumSpeedKph = Mathf.Clamp(OptionalFloat(command, "minimumSpeedKph", 1f),
            0.1f, fixture.MaximumSpeedKph);
        // A cold DE2 carrying a fully materialized item lab needs materially longer than 25
        // seconds to reach road speed. Keep this a barrier, not an acceleration benchmark.
        float timeoutSeconds = Mathf.Clamp(OptionalFloat(command, "waitSeconds", 20f), 1f, 60f);
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        float speedKph = 0f;
        while (Time.realtimeSinceStartup <= deadline)
        {
            TrainCar locomotive = fixture.Cars.FirstOrDefault()?.Car;
            speedKph = locomotive?.rb == null ? 0f : locomotive.rb.velocity.magnitude * 3.6f;
            if (speedKph >= minimumSpeedKph) break;
            yield return null;
        }
        bool moving = speedKph >= minimumSpeedKph;
        lock (run)
        {
            run.Result["fixtureId"] = fixtureId;
            run.Result["moving"] = moving;
            run.Result["speedKph"] = speedKph;
            run.Result["minimumSpeedKph"] = minimumSpeedKph;
            run.Result["fixture"] = Snapshot(fixture);
        }
        if (!moving)
            throw new InvalidOperationException("fixture-motion-timeout:" + speedKph.ToString("F3",
                CultureInfo.InvariantCulture));
    }

    public IEnumerator CleanupOrphans(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        int removed = 0;
        int occupied = 0;
        applyingRouteLease = true;
        try
        {
            foreach (DebugTrainFixtureRecord fixture in fixtures.Values)
            foreach (DebugTrainRouteJunctionRecord route in fixture.Junctions)
            {
                if (route.Junction != null)
                    route.Junction.Switch(Junction.SwitchMode.NO_SOUND, (byte)route.OriginalBranch);
                leasedJunctions.Remove(route.Junction);
            }
        }
        finally { applyingRouteLease = false; }
        foreach (DebugTrainFixtureTag tag in Resources.FindObjectsOfTypeAll<DebugTrainFixtureTag>()
                     .Where(tag => tag != null && tag.gameObject.scene.IsValid()).ToArray())
        {
            TrainCar car = tag?.GetComponent<TrainCar>() ?? tag?.GetComponentInChildren<TrainCar>();
            if (car == null) continue;
            if (car.TryNetworked(out NetworkedTrainCar occupiedCar) && occupiedCar.HasPlayers)
            {
                occupied++;
                continue;
            }
            protectedCars.Remove(car);
            DebugTrainFixtureTag fixtureTag = car.GetComponent<DebugTrainFixtureTag>();
            if (fixtureTag != null) UnityEngine.Object.Destroy(fixtureTag);
            CarSpawner.Instance.DeleteCar(car);
            removed++;
            yield return new WaitForEndOfFrame();
            yield return null;
        }
        DebugTrainFixtureTag[] remaining = Resources.FindObjectsOfTypeAll<DebugTrainFixtureTag>()
            .Where(tag => tag != null && tag.gameObject.scene.IsValid()).ToArray();
        foreach (DebugTrainFixtureRecord fixture in fixtures.Values)
        {
            if (remaining.Any(tag => tag.FixtureId == fixture.FixtureId)) continue;
            fixture.State = DebugTrainFixtureState.Clean;
            fixture.Failure = string.Empty;
            fixture.Junctions.Clear();
        }
        lock (run)
        {
            run.Result["removedCarCount"] = removed;
            run.Result["occupiedCarCount"] = occupied;
            run.Result["remainingTagCount"] = remaining.Length;
            run.Result["cleanupClean"] = remaining.Length == 0;
        }
        if (remaining.Length != 0)
            throw new InvalidOperationException("fixture-orphan-cleanup-incomplete:" + remaining.Length);
    }

    public IEnumerator Start(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        DebugTrainFixtureRecord fixture = RequiredFixture(command);
        TrainCar locomotive = fixture.Cars.FirstOrDefault()?.Car ??
            throw new InvalidOperationException("fixture-locomotive-unavailable");
        StartupHelper.Startup(locomotive);
        fixture.State = DebugTrainFixtureState.Running;
        fixture.ControllerState = DebugTrainSpeedControllerState.Starting;
        fixture.TargetSpeedKph = Mathf.Clamp(OptionalFloat(command, "targetSpeedKph", 20f), 0f,
            fixture.MaximumSpeedKph);
        fixture.ControllerReason = "start-requested";
        SetControl(locomotive, "Reverser", OptionalFloat(command, "reverser", 1f));
        Publish(fixture, "train.fixture.started");
        yield return null;
        lock (run) run.Result["fixture"] = Snapshot(fixture);
    }

    public IEnumerator Stop(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        DebugTrainFixtureRecord fixture = RequiredFixture(command);
        fixture.TargetSpeedKph = 0f;
        fixture.ControllerState = DebugTrainSpeedControllerState.Braking;
        fixture.ControllerReason = "stop-requested";
        yield return null;
        lock (run) run.Result["fixture"] = Snapshot(fixture);
    }

    public IEnumerator SetFixtureControl(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        DebugTrainFixtureRecord fixture = RequiredFixture(command);
        TrainCar locomotive = fixture.Cars.FirstOrDefault()?.Car ??
            throw new InvalidOperationException("fixture-locomotive-unavailable");
        string control = Required(command, "control");
        float value = OptionalFloat(command, "value", float.NaN);
        if (float.IsNaN(value)) throw new ArgumentException("missing-or-invalid-parameter:value");
        if (!SetControl(locomotive, control, value))
            throw new InvalidOperationException("fixture-control-unavailable:" + control);
        fixture.ControllerReason = "manual-control:" + control;
        Publish(fixture, "train.fixture.control-applied");
        yield return null;
        lock (run)
        {
            run.Result["control"] = control;
            run.Result["value"] = value;
            run.Result["fixture"] = Snapshot(fixture);
        }
    }

    public IEnumerator Relocate(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        DebugTrainFixtureRecord fixture = RequiredFixture(command);
        if (fixture.State == DebugTrainFixtureState.Running)
            throw new InvalidOperationException("fixture-must-be-stopped-before-relocation");
        string locationId = Optional(command, "locationId", string.Empty);
        DebugTrainFixtureLocationRecord location = string.IsNullOrWhiteSpace(locationId) ? null :
            DebugTrainFixtureLocationCatalog.Resolve(locationId);
        DebugTrainTrackRecord destination = DebugTrainTrackCatalog.Resolve(
            location?.TrackId ?? Required(command, "trackId"),
            location?.AllowGenericTrack ?? OptionalBool(command, "allowGenericTrack", false),
            location?.GeometryHash ?? Optional(command, "geometryHash", string.Empty));
        int pointIndex = location?.PointIndex ?? RequiredInt(command, "pointIndex");
        bool withTrack = location?.WithTrackDirection ??
            OptionalBool(command, "withTrackDirection", fixture.WithTrackDirection);
        var points = destination.Track.GetKinkedPointSet()?.points ??
            throw new InvalidOperationException("destination-track-point-set-unavailable");
        if (pointIndex < 0 || pointIndex >= points.Length)
            throw new InvalidOperationException("track-point-index-out-of-range:" + pointIndex);

        string operationId = "train-relocation-" + Guid.NewGuid().ToString("N");
        Publish(fixture, "train-relocation.started");
        int cursor = pointIndex;
        float previousHalfLength = 0f;
        applyingAtomicRelocation = true;
        try
        {
            for (int index = 0; index < fixture.Cars.Count; index++)
            {
                TrainCar car = fixture.Cars[index].Car ??
                    throw new InvalidOperationException("fixture-car-unavailable:" + index);
                Bounds bounds = CarSpawner.GetBoundsOfCar(car.gameObject);
                float halfLength = Mathf.Max(2f, bounds.extents.z);
                if (index > 0)
                {
                    float spacing = previousHalfLength + halfLength + 1f;
                    cursor += (withTrack ? -1 : 1) * Mathf.CeilToInt(spacing /
                        Mathf.Max(0.1f, (float)points[Mathf.Clamp(cursor, 0, points.Length - 1)].spanToNextPoint));
                }
                if (cursor < 0 || cursor >= points.Length)
                    throw new InvalidOperationException("relocation-consist-exceeds-track:" + index);
                Vector3 forward = withTrack ? points[cursor].forward : -points[cursor].forward;
                car.MoveToTrackWithCarUncouple(destination.Track,
                    (Vector3)points[cursor].position + WorldMover.currentMove, forward);
                ApplySafeStationaryState(car);
                previousHalfLength = halfLength;
                yield return null;
            }
        }
        finally { applyingAtomicRelocation = false; }

        for (int index = 0; index + 1 < fixture.Cars.Count; index++)
        {
            Coupler first = withTrack ? fixture.Cars[index].Car.rearCoupler :
                fixture.Cars[index].Car.frontCoupler;
            Coupler second = withTrack ? fixture.Cars[index + 1].Car.frontCoupler :
                fixture.Cars[index + 1].Car.rearCoupler;
            NetworkedCarSpawner.SetCouplingState(first, second,
                ChainCouplerInteraction.State.Attached_Tight);
        }
        Physics.SyncTransforms();

        List<TrainCar> consist = fixture.Cars.Select(record => record.Car).ToList();
        fixture.RelocationRevision++;
        fixture.LastRelocationOperationId = operationId;
        fixture.Track = destination;
        fixture.LocationId = location?.Id ?? string.Empty;
        fixture.PointIndex = pointIndex;
        fixture.WithTrackDirection = withTrack;
        TrainsetSpawnPart[] committedCars = TrainsetSpawnPart.FromTrainSet(consist);
        ClientboundTrainsetRelocationPacket packet = new()
        {
            OperationId = operationId,
            Revision = fixture.RelocationRevision,
            HostTick = NetworkLifecycle.Instance.Tick,
            RootNetId = fixture.Cars[0].NetId,
            Cars = committedCars,
            CommittedHash = TrainsetRelocationHash.Compute(committedCars)
        };
        NetworkLifecycle.Instance.Server.SendTrainsetRelocation(packet);
        DebugRuntime.Publish("train-relocation", "train-relocation.host-committed",
            DebugRuntimeSide.Server, entityId: "train:" + packet.RootNetId,
            correlationId: operationId, data: new()
            {
                ["revision"] = packet.Revision, ["carCount"] = packet.Cars.Length,
                ["trackId"] = destination.GameObjectId, ["pointIndex"] = pointIndex
            });
        lock (run)
        {
            run.Result["operationId"] = operationId;
            run.Result["revision"] = fixture.RelocationRevision;
            run.Result["fixture"] = Snapshot(fixture);
        }
    }

    public IEnumerator AcquireRoute(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireHost();
        DebugTrainFixtureRecord fixture = RequiredFixture(command);
        if (fixture.Junctions.Count != 0) throw new InvalidOperationException("route-already-leased");
        string assignments = Required(command, "junctions");
        foreach (string assignment in assignments.Split(','))
        {
            string[] parts = assignment.Split(':');
            if (parts.Length != 2 || !ushort.TryParse(parts[0], out ushort netId) ||
                !int.TryParse(parts[1], out int branch) || branch < 0 || branch > byte.MaxValue ||
                !NetworkedJunction.Get(netId, out NetworkedJunction networked))
                throw new InvalidOperationException("invalid-route-junction:" + assignment);
            fixture.Junctions.Add(new DebugTrainRouteJunctionRecord
            {
                NetId = netId, Junction = networked.Junction,
                OriginalBranch = networked.Junction.selectedBranch, LeasedBranch = branch
            });
        }
        applyingRouteLease = true;
        try
        {
            foreach (DebugTrainRouteJunctionRecord junction in fixture.Junctions)
            {
                leasedJunctions.Add(junction.Junction);
                junction.Junction.Switch(Junction.SwitchMode.NO_SOUND, (byte)junction.LeasedBranch);
            }
        }
        finally { applyingRouteLease = false; }
        Publish(fixture, "train.fixture.route-acquired");
        yield return null;
        lock (run) run.Result["fixture"] = Snapshot(fixture);
    }

    public void Tick()
    {
        foreach (DebugTrainFixtureRecord fixture in fixtures.Values.ToArray())
        {
            if (fixture.State is DebugTrainFixtureState.Clean or DebugTrainFixtureState.Cleaning) continue;
            MaintainSafety(fixture);
            MaintainRoute(fixture);
            MaintainSpeed(fixture);
        }
    }

    private static void MaintainSafety(DebugTrainFixtureRecord fixture)
    {
        foreach (DebugTrainFixtureCarRecord record in fixture.Cars)
        {
            TrainCar car = record.Car;
            if (car == null) continue;
            if (fixture.ProtectFuses)
            {
                var flow = car.SimController?.SimulationFlow;
                if (flow != null)
                    foreach (var fuse in flow.AllFuses)
                        if (!fuse.State) fuse.ChangeState(true);
            }
            if (fixture.PreventDamage) RestoreFixtureHealth(car);
        }
        if (fixture.SustainEngine && fixture.State == DebugTrainFixtureState.Running &&
            Time.unscaledTime >= fixture.NextEngineSustainTime)
        {
            fixture.NextEngineSustainTime = Time.unscaledTime + 1f;
            TrainCar locomotive = fixture.Cars.FirstOrDefault()?.Car;
            var controls = locomotive?.SimController?.controlsOverrider;
            if (locomotive != null && controls?.EngineOnReader != null &&
                !controls.EngineOnReader.IsOn)
                StartupHelper.Startup(locomotive);
        }
    }

    private static void RestoreFixtureHealth(TrainCar car)
    {
        DamageController damage = car.GetComponent<DamageController>();
        if (damage != null)
        {
            damage.bodyDamage?.LoadCarDamageState(1f);
            damage.wheels?.SetCurrentHealthPercentage(1f);
            damage.mechanicalPT?.SetCurrentHealthPercentage(1f);
            damage.electricalPT?.SetCurrentHealthPercentage(1f);
            if (damage.windows != null) damage.windows.windowsBroken = false;
            return;
        }

        car.GetComponent<CarDamageModel>()?.SetHealth(1f);
    }

    private static void MaintainRoute(DebugTrainFixtureRecord fixture)
    {
        if (fixture.Junctions.Count == 0) return;
        applyingRouteLease = true;
        try
        {
            foreach (DebugTrainRouteJunctionRecord record in fixture.Junctions)
                if (record.Junction != null && record.Junction.selectedBranch != record.LeasedBranch)
                    record.Junction.Switch(Junction.SwitchMode.NO_SOUND, (byte)record.LeasedBranch);
        }
        finally { applyingRouteLease = false; }
    }

    private static void MaintainSpeed(DebugTrainFixtureRecord fixture)
    {
        if (fixture.State != DebugTrainFixtureState.Running || fixture.Cars.Count == 0) return;
        TrainCar locomotive = fixture.Cars[0].Car;
        if (locomotive == null) return;
        float speedKph = Mathf.Abs(Vector3.Dot(locomotive.transform.forward,
            locomotive.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero)) * 3.6f;
        if (speedKph > fixture.MaximumSpeedKph + 2f)
        {
            fixture.ControllerState = DebugTrainSpeedControllerState.EmergencyStop;
            fixture.ControllerReason = "maximum-speed-exceeded";
            SetControl(locomotive, "Throttle", 0f);
            SetControl(locomotive, "DynamicBrake", 1f);
            SetControl(locomotive, "TrainBrake", 1f);
            return;
        }
        float error = fixture.TargetSpeedKph - speedKph;
        if (fixture.TargetSpeedKph <= 0.1f)
        {
            SetControl(locomotive, "Throttle", 0f);
            SetControl(locomotive, "DynamicBrake", speedKph > 2f ? 0.7f : 0f);
            SetControl(locomotive, "TrainBrake", speedKph > 0.5f ? 0.6f : 1f);
            fixture.ControllerState = speedKph <= 0.5f ? DebugTrainSpeedControllerState.Stopped :
                DebugTrainSpeedControllerState.Braking;
            if (speedKph <= 0.5f) fixture.State = DebugTrainFixtureState.Ready;
        }
        else if (error > 2f)
        {
            SetControl(locomotive, "TrainBrake", 0f);
            SetControl(locomotive, "DynamicBrake", 0f);
            SetControl(locomotive, "Throttle", Mathf.Clamp(error / 20f, 0.15f, 0.65f));
            fixture.ControllerState = DebugTrainSpeedControllerState.Accelerating;
        }
        else if (error < -2f)
        {
            SetControl(locomotive, "Throttle", 0f);
            SetControl(locomotive, "DynamicBrake", Mathf.Clamp(-error / 15f, 0.15f, 0.75f));
            fixture.ControllerState = DebugTrainSpeedControllerState.Braking;
        }
        else
        {
            SetControl(locomotive, "DynamicBrake", 0f);
            SetControl(locomotive, "TrainBrake", 0f);
            SetControl(locomotive, "Throttle", 0.12f);
            fixture.ControllerState = DebugTrainSpeedControllerState.Cruising;
        }
    }

    private IEnumerator CleanupRecord(DebugTrainFixtureRecord fixture)
    {
        fixture.State = DebugTrainFixtureState.Cleaning;
        fixture.TargetSpeedKph = 0f;
        TrainCar localCar = PlayerManager.Car;
        if (localCar != null && fixture.Cars.Any(record => record.Car == localCar))
        {
            Vector3 destination = localCar.transform.position + localCar.transform.right * 12f +
                Vector3.up;
            PlayerManager.TeleportPlayer(destination, localCar.transform.rotation, null, true, false);
            yield return null;
            yield return new WaitForEndOfFrame();
        }
        DebugTrainFixtureCarRecord occupiedRecord = fixture.Cars.FirstOrDefault(record =>
            record.Car != null && record.Car.TryNetworked(out NetworkedTrainCar networked) &&
            networked.HasPlayers);
        if (occupiedRecord != null)
        {
            fixture.State = DebugTrainFixtureState.Failed;
            fixture.Failure = "fixture-car-still-occupied:" + occupiedRecord.NetId;
            Publish(fixture, "train.fixture.cleanup-blocked", occupiedRecord,
                DebugSeverity.Error);
            throw new InvalidOperationException(fixture.Failure);
        }
        applyingRouteLease = true;
        try
        {
            for (int index = fixture.Junctions.Count - 1; index >= 0; index--)
            {
                DebugTrainRouteJunctionRecord record = fixture.Junctions[index];
                if (record.Junction != null)
                    record.Junction.Switch(Junction.SwitchMode.NO_SOUND, (byte)record.OriginalBranch);
                leasedJunctions.Remove(record.Junction);
            }
            fixture.Junctions.Clear();
        }
        finally { applyingRouteLease = false; }

        for (int index = fixture.Cars.Count - 1; index >= 0; index--)
        {
            TrainCar car = fixture.Cars[index].Car;
            if (car == null) continue;
            protectedCars.Remove(car);
            DebugTrainFixtureTag fixtureTag = car.GetComponent<DebugTrainFixtureTag>();
            if (fixtureTag != null) UnityEngine.Object.Destroy(fixtureTag);
            CarSpawner.Instance.DeleteCar(car);
            yield return new WaitForEndOfFrame();
            yield return null;
        }
        yield return new WaitForEndOfFrame();
        yield return null;
        float deadline = Time.realtimeSinceStartup + 10f;
        while (fixture.Cars.Any(record => record.NetId != 0 &&
                   NetworkedTrainCar.TryGet(record.NetId, out TrainCar car) && car != null) &&
               Time.realtimeSinceStartup <= deadline)
            yield return null;
        bool leaked = fixture.Cars.Any(record => record.NetId != 0 &&
            NetworkedTrainCar.TryGet(record.NetId, out TrainCar car) && car != null);
        int remainingTags = Resources.FindObjectsOfTypeAll<DebugTrainFixtureTag>().Count(tag =>
            tag != null && tag.gameObject.scene.IsValid() && tag.FixtureId == fixture.FixtureId);
        leaked |= remainingTags != 0;
        fixture.State = leaked ? DebugTrainFixtureState.Failed : DebugTrainFixtureState.Clean;
        fixture.Failure = leaked ? "fixture-cleanup-verification-failed:tags=" + remainingTags : string.Empty;
        Publish(fixture, leaked ? "train.fixture.cleanup-failed" : "train.fixture.cleaned",
            severity: leaked ? DebugSeverity.Error : DebugSeverity.Info);
        if (leaked) throw new InvalidOperationException(fixture.Failure);
    }

    private static void ApplySafeStationaryState(TrainCar car)
    {
        if (car?.brakeSystem == null) return;
        if (car.brakeSystem.hasHandbrake) car.brakeSystem.SetHandbrakePosition(1f);
        if (car.brakeSystem.hasTrainBrake) car.brakeSystem.trainBrakePosition = 1f;
        car.brakeSystem.ForceCylinderPressure(1f);
    }

    private static bool IsNetworkReady(TrainCar car) =>
        car != null && NetworkedTrainCar.TryGetFromTrainCar(car, out NetworkedTrainCar networked) &&
        networked.Client_Initialized;

    private static bool SetControl(TrainCar car, string name, float value)
    {
        object overrider = car?.SimController?.controlsOverrider;
        if (overrider == null) return false;
        PropertyInfo property = overrider.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object control = property?.GetValue(overrider, null);
        MethodInfo set = control?.GetType().GetMethod("Set", BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(float) }, null);
        if (set == null) return false;
        set.Invoke(control, new object[] { value });
        return true;
    }

    private DebugTrainFixtureRecord RequiredFixture(RuntimeTestCommandDto command)
    {
        string fixtureId = Required(command, "fixtureId");
        return fixtures.TryGetValue(fixtureId, out DebugTrainFixtureRecord fixture) ? fixture :
            throw new InvalidOperationException("fixture-not-found:" + fixtureId);
    }

    private static Dictionary<string, object> Snapshot(DebugTrainFixtureRecord fixture) => new(StringComparer.Ordinal)
    {
        ["fixtureId"] = fixture.FixtureId,
        ["runId"] = fixture.RunId,
        ["state"] = fixture.State.ToString(),
        ["createdUtc"] = fixture.CreatedUtc.ToString("O"),
        ["track"] = fixture.Track == null ? null : DebugTrainTrackCatalog.Snapshot(fixture.Track, fixture.PointIndex),
        ["locationId"] = fixture.LocationId ?? string.Empty,
        ["withTrackDirection"] = fixture.WithTrackDirection,
        ["preventDerailment"] = fixture.PreventDerailment,
        ["protectFuses"] = fixture.ProtectFuses,
        ["sustainEngine"] = fixture.SustainEngine,
        ["preventDamage"] = fixture.PreventDamage,
        ["maximumSpeedKph"] = fixture.MaximumSpeedKph,
        ["targetSpeedKph"] = fixture.TargetSpeedKph,
        ["speedControllerState"] = fixture.ControllerState.ToString(),
        ["controllerReason"] = fixture.ControllerReason ?? string.Empty,
        ["relocationRevision"] = fixture.RelocationRevision,
        ["lastRelocationOperationId"] = fixture.LastRelocationOperationId ?? string.Empty,
        ["failure"] = fixture.Failure ?? string.Empty,
        ["junctions"] = fixture.Junctions.Select(record => new Dictionary<string, object>
        {
            ["netId"] = record.NetId, ["originalBranch"] = record.OriginalBranch,
            ["leasedBranch"] = record.LeasedBranch, ["name"] = record.Junction?.name ?? string.Empty
        }).ToArray(),
        ["cars"] = fixture.Cars.Select(record => new Dictionary<string, object>
        {
            ["index"] = record.Index, ["role"] = record.Role, ["liveryId"] = record.LiveryId,
            ["netId"] = record.NetId, ["carId"] = record.CarId ?? string.Empty,
            ["carGuid"] = record.CarGuid ?? string.Empty,
            ["alive"] = record.Car != null,
            ["positionAbsolute"] = record.Car == null ? null :
                DebugValueSnapshotter.Snapshot(record.Car.transform.position - WorldMover.currentMove),
            ["speedKph"] = record.Car?.rb == null ? 0f : record.Car.rb.velocity.magnitude * 3.6f,
            ["derailed"] = record.Car?.derailed == true,
            ["hasPlayers"] = record.Car != null && record.Car.TryNetworked(out NetworkedTrainCar networked) &&
                networked.HasPlayers,
            ["bogieTrackNetIds"] = record.Car == null ? Array.Empty<ushort>() :
                record.Car.Bogies.Select(bogie => bogie.track != null &&
                    NetworkedRailTrack.TryGetNetId(bogie.track, out ushort id) ? id : (ushort)0).ToArray(),
            ["frontCoupledNetId"] = record.Car?.frontCoupler?.coupledTo?.train?.GetNetId() ?? (ushort)0,
            ["rearCoupledNetId"] = record.Car?.rearCoupler?.coupledTo?.train?.GetNetId() ?? (ushort)0
        }).ToArray(),
        ["netIds"] = string.Join(",", fixture.Cars.Select(record => record.NetId))
    };

    private static void Publish(DebugTrainFixtureRecord fixture, string eventName,
        DebugTrainFixtureCarRecord car = null, DebugSeverity severity = DebugSeverity.Info)
    {
        Dictionary<string, object> data = new()
        {
            ["fixtureId"] = fixture.FixtureId,
            ["state"] = fixture.State.ToString(),
            ["trackId"] = fixture.Track?.GameObjectId ?? string.Empty,
            ["carCount"] = fixture.Cars.Count
        };
        if (car != null) { data["carIndex"] = car.Index; data["netId"] = car.NetId; data["liveryId"] = car.LiveryId; }
        DebugRuntime.Publish("train-fixture", eventName, DebugRuntimeSide.Server, severity,
            entityId: "train-fixture:" + fixture.FixtureId, correlationId: fixture.RunId, data: data);
    }

    private static void RequireHost()
    {
        if (NetworkLifecycle.Instance?.IsServerRunning != true || !NetworkLifecycle.Instance.IsHost())
            throw new InvalidOperationException("train-fixture-host-authority-required");
    }
    private static string Required(RuntimeTestCommandDto command, string key) =>
        command.Parameters != null && command.Parameters.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim() : throw new ArgumentException("missing-or-invalid-parameter:" + key);
    private static string Optional(RuntimeTestCommandDto command, string key, string fallback) =>
        command.Parameters != null && command.Parameters.TryGetValue(key, out string value) ? value : fallback;
    private static int RequiredInt(RuntimeTestCommandDto command, string key) =>
        int.TryParse(Required(command, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value :
            throw new ArgumentException("missing-or-invalid-parameter:" + key);
    private static int RequiredOrDefaultInt(RuntimeTestCommandDto command, string key, int fallback) =>
        int.TryParse(Optional(command, key, string.Empty), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private static float OptionalFloat(RuntimeTestCommandDto command, string key, float fallback) =>
        float.TryParse(Optional(command, key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
    private static bool OptionalBool(RuntimeTestCommandDto command, string key, bool fallback) =>
        bool.TryParse(Optional(command, key, string.Empty), out bool value) ? value : fallback;
}
#endif
