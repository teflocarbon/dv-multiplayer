#if DEBUG
using Multiplayer.Debugging.Protocol;
using Multiplayer.Debugging.RuntimeTests.TrainFixtures;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

internal sealed class TrainFixtureRuntimeScenarioDriver
{
    private const string LocationId = "steel-mill-north-test-track";
    private const string TrackId = "[Y]_[#Y]_[#S-550-#T]";
    private const string GeometryHash = "15844e45f342676bf9fac0d360d8eae61cc99099f3deae2e1ad40e3198bc47b5";

    public IEnumerator AnchorValidation(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RuntimeTestScenarioContext context = new(command, run);
        return RuntimeTestScenarioRunner.Run(context, AnchorValidationBody(context, run));
    }

    public IEnumerator SingleCarLifecycle(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        Lifecycle(command, run, "LocoDE2", "Locomotive", false, false);

    public IEnumerator CoupledConsistLifecycle(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        Lifecycle(command, run, "LocoDE2,AutorackBlue", "Locomotive,TestCar", true, false);

    public IEnumerator AtomicRelocation(RuntimeTestCommandDto command, RuntimeTestRunDto run) =>
        Lifecycle(command, run, "LocoDE2,AutorackBlue", "Locomotive,TestCar", true, true);

    public IEnumerator StartupSafety(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RuntimeTestScenarioContext context = new(command, run);
        return RuntimeTestScenarioRunner.Run(context, StartupSafetyBody(command, run, context));
    }

    private static IEnumerator AnchorValidationBody(RuntimeTestScenarioContext context,
        RuntimeTestRunDto run)
    {
        IEnumerator prepare = PrepareLocation(context);
        while (prepare.MoveNext()) yield return prepare.Current;
        context.EnterPhase("arrange", "resolve-named-track-anchor");
        DebugTrainFixtureLocationRecord location = DebugTrainFixtureLocationCatalog.Resolve(LocationId);
        DebugTrainTrackRecord track = DebugTrainTrackCatalog.Resolve(location.TrackId,
            location.AllowGenericTrack, location.GeometryHash);
        context.Assert("named-location-resolves", track != null, location.Id);
        context.Assert("geometry-hash-matches", string.Equals(track.GeometryHash,
            GeometryHash, StringComparison.Ordinal), track.GeometryHash);
        context.Assert("spawn-point-in-range", location.PointIndex >= 0 &&
            location.PointIndex < track.PointCount, location.PointIndex);
        context.Assert("track-has-consist-clearance", track.Span >= 250d, track.Span);
        lock (run) run.Result["location"] = DebugTrainFixtureLocationCatalog.Snapshot(location);
        yield return null;
    }

    private static IEnumerator Lifecycle(RuntimeTestCommandDto command, RuntimeTestRunDto run,
        string liveries, string roles, bool expectCoupled, bool relocate)
    {
        RuntimeTestScenarioContext context = new(command, run);
        return RuntimeTestScenarioRunner.Run(context,
            LifecycleBody(command, run, context, liveries, roles, expectCoupled, relocate));
    }

    private static IEnumerator LifecycleBody(RuntimeTestCommandDto command, RuntimeTestRunDto run,
        RuntimeTestScenarioContext context, string liveries, string roles, bool expectCoupled,
        bool relocate)
    {
        DebugTrainFixtureManager manager = Manager();
        string fixtureId = "scenario-train-" + Guid.NewGuid().ToString("N");
        IEnumerator prepare = PrepareLocation(context);
        while (prepare.MoveNext()) yield return prepare.Current;
        context.EnterPhase("arrange", "create-authoritative-consist");
        yield return manager.Create(Subcommand(command, new()
        {
            ["fixtureId"] = fixtureId, ["locationId"] = LocationId,
            ["liveries"] = liveries, ["roles"] = roles,
            ["preventDerailment"] = "true", ["protectFuses"] = "true",
            ["preventDamage"] = "true", ["sustainEngine"] = "true"
        }), run);
        context.RegisterResource("train-fixture", fixtureId, "deleted-and-unprojected",
            () => manager.Cleanup(Subcommand(command, new() { ["fixtureId"] = fixtureId }), run));

        context.Assert("fixture-ledger-present", manager.TryGetFixture(fixtureId, out var fixture));
        context.Assert("fixture-ready", fixture.State == DebugTrainFixtureState.Ready,
            fixture.State.ToString());
        int expectedCars = liveries.Split(',').Length;
        context.Assert("fixture-car-count", fixture.Cars.Count == expectedCars, fixture.Cars.Count);
        context.Assert("fixture-network-ids-assigned", fixture.Cars.TrueForAll(car => car.NetId != 0),
            fixture.Cars.ConvertAll(car => car.NetId));
        context.Assert("fixture-cars-alive", fixture.Cars.TrueForAll(car => car.Car != null));
        if (expectCoupled)
            context.Assert("ordered-consist-coupled", Coupled(fixture), CouplingSnapshot(fixture));

        if (relocate)
        {
            context.EnterPhase("act", "relocate-consist-atomically");
            yield return manager.Relocate(Subcommand(command, new()
            {
                ["fixtureId"] = fixtureId, ["trackId"] = TrackId,
                ["pointIndex"] = "300", ["withTrackDirection"] = "true",
                ["allowGenericTrack"] = "true", ["geometryHash"] = GeometryHash
            }), run);
            context.Assert("relocation-revision-advanced", fixture.RelocationRevision == 1,
                fixture.RelocationRevision);
            context.Assert("relocation-operation-recorded",
                !string.IsNullOrWhiteSpace(fixture.LastRelocationOperationId),
                fixture.LastRelocationOperationId);
            context.Assert("relocated-point-committed", fixture.PointIndex == 300,
                fixture.PointIndex);
            context.Assert("relocated-consist-remains-coupled", Coupled(fixture),
                CouplingSnapshot(fixture));
        }
        yield return null;
    }

    private static IEnumerator StartupSafetyBody(RuntimeTestCommandDto command,
        RuntimeTestRunDto run, RuntimeTestScenarioContext context)
    {
        DebugTrainFixtureManager manager = Manager();
        string fixtureId = "scenario-train-safety-" + Guid.NewGuid().ToString("N");
        IEnumerator prepare = PrepareLocation(context);
        while (prepare.MoveNext()) yield return prepare.Current;
        context.EnterPhase("arrange", "create-protected-locomotive");
        yield return manager.Create(Subcommand(command, new()
        {
            ["fixtureId"] = fixtureId, ["locationId"] = LocationId,
            ["liveries"] = "LocoDE2", ["roles"] = "Locomotive",
            ["preventDerailment"] = "true", ["protectFuses"] = "true",
            ["preventDamage"] = "true", ["sustainEngine"] = "true",
            ["maximumSpeedKph"] = "12"
        }), run);
        context.RegisterResource("train-fixture", fixtureId, "deleted-and-unprojected",
            () => manager.Cleanup(Subcommand(command, new() { ["fixtureId"] = fixtureId }), run));
        manager.TryGetFixture(fixtureId, out DebugTrainFixtureRecord fixture);

        context.EnterPhase("act", "start-bounded-controller");
        yield return manager.Start(Subcommand(command, new()
        {
            ["fixtureId"] = fixtureId, ["targetSpeedKph"] = "4", ["reverser"] = "1"
        }), run);
        float deadline = Time.realtimeSinceStartup + 5f;
        while (Time.realtimeSinceStartup < deadline) yield return null;
        context.Assert("protected-car-not-derailed", fixture.Cars[0].Car?.derailed == false,
            fixture.Cars[0].Car?.derailed);
        context.Assert("speed-controller-bounded",
            fixture.ControllerState != DebugTrainSpeedControllerState.EmergencyStop,
            fixture.ControllerReason);
        context.Assert("required-fuses-closed", FusesClosed(fixture.Cars[0].Car));

        context.EnterPhase("act", "stop-bounded-controller");
        yield return manager.Stop(Subcommand(command, new() { ["fixtureId"] = fixtureId }), run);
        yield return null;
    }

    private static IEnumerator PrepareLocation(RuntimeTestScenarioContext context)
    {
        if (PlayerManager.PlayerTransform == null)
            throw new InvalidOperationException("player-transform-unavailable");
        Vector3 originalAbsolute = PlayerManager.PlayerTransform.position - WorldMover.currentMove;
        Quaternion originalRotation = PlayerManager.PlayerTransform.rotation;
        context.RegisterResource("player-position", "host", "restored",
            () => RestorePlayer(originalAbsolute, originalRotation));

        context.EnterPhase("arrange", "stream-named-track-location");
        DebugTrainFixtureLocationRecord location =
            DebugTrainFixtureLocationCatalog.Resolve(LocationId);
        IEnumerator load = DebugTrainFixtureLocationCatalog.TeleportAndWaitForTrack(location);
        while (load.MoveNext()) yield return load.Current;
        context.Assert("named-track-location-streamed", true, location.Id);
    }

    private static IEnumerator RestorePlayer(Vector3 absolute, Quaternion rotation)
    {
        if (PlayerManager.PlayerTransform != null)
        {
            PlayerManager.TeleportPlayer(absolute + WorldMover.currentMove, rotation,
                null, true, false);
            yield return null;
            yield return new WaitForEndOfFrame();
        }
    }

    private static bool Coupled(DebugTrainFixtureRecord fixture)
    {
        for (int index = 0; index + 1 < fixture.Cars.Count; index++)
            if (fixture.Cars[index].Car?.rearCoupler?.coupledTo !=
                fixture.Cars[index + 1].Car?.frontCoupler)
                return false;
        return true;
    }

    private static object CouplingSnapshot(DebugTrainFixtureRecord fixture) =>
        fixture.Cars.ConvertAll(car => new
        {
            car.Index,
            front = car.Car?.frontCoupler?.coupledTo?.train?.GetNetId() ?? 0,
            rear = car.Car?.rearCoupler?.coupledTo?.train?.GetNetId() ?? 0
        });

    private static bool FusesClosed(TrainCar car)
    {
        var fuses = car?.SimController?.SimulationFlow?.AllFuses;
        if (fuses == null) return true;
        for (int index = 0; index < fuses.Count; index++) if (!fuses[index].State) return false;
        return true;
    }

    private static DebugTrainFixtureManager Manager() =>
        DebugTrainFixtureManager.Current ?? throw new InvalidOperationException("train-fixture-manager-unavailable");

    private static RuntimeTestCommandDto Subcommand(RuntimeTestCommandDto source,
        Dictionary<string, string> parameters) => new()
    {
        RequestId = source.RequestId,
        RunId = source.RunId,
        CaseId = source.CaseId,
        Command = source.Command,
        TimeoutMilliseconds = source.TimeoutMilliseconds,
        Parameters = parameters
    };
}
#endif
