using System;
using System.Collections.Generic;
using System.Diagnostics;
using LocoSim.Definitions;
using LocoSim.Implementations;
using Multiplayer.Core.Trains;
using Multiplayer.Networking.Managers.Client;
using Multiplayer.Patches.World;
using UnityEngine;

namespace Multiplayer.Components.Networking.Train;

/// <summary>
/// How a client shows a car's simulation outputs: its gauges, sounds and animations.
///
/// Clients don't run the simulation (<see cref="SimComponent_Tick_Patch"/>): they show the
/// host's port values, which arrive 24 times a second, unevenly, a round trip after a player's
/// control changed them. Two things make that look and sound right (docs/design/trains.md,
/// "Showing the simulation on clients"):
/// <list type="bullet">
/// <item>Prediction. While the local player is driving the car's train, the outputs that are a
/// function of controls and slowly changing host state (whistle, horn, bells, dynamo, sanders,
/// smoothed controls, the reverser's readout) are worked out here every frame, so a pull on the
/// whistle cord sounds at once, tapering as it does on the host. The host's values for them are
/// kept but not shown.</item>
/// <item>Playout. Every other output the host sends is stamped with the host's tick and shown a
/// little behind the newest, interpolated between ticks, so it moves smoothly rather than in
/// uneven steps. The delay is sized from the jitter measured on arrivals (<see cref="PlayoutClock"/>)
/// and is zero for a train the local player is driving: they're never delayed.</item>
/// </list>
/// The host still decides everything physical (torque, motion, pressures); this is only what a
/// client shows of it.
/// </summary>
public sealed class TrainPortPresenter
{
    private const double MIN_DELAY_SECONDS = 2.0 / NetworkLifecycle.TICK_RATE;
    private const double TICK_SECONDS = 1.0 / NetworkLifecycle.TICK_RATE;
    // A train is being driven by the local player for this long after their last control change
    // on it (or while they hold a control's lease).
    private const float DRIVING_HOLD_SECONDS = 4f;

    private static readonly Stopwatch clockTime = Stopwatch.StartNew();
    private static PlayoutClock clock;
    private static NetworkClient clockOwner;
    private static readonly Dictionary<SimulationFlow, TrainPortPresenter> byFlow = [];

    public static double Now => clockTime.Elapsed.TotalSeconds;

    /// <summary>The host's port timeline for the current connection.</summary>
    public static PlayoutClock Clock
    {
        get
        {
            NetworkClient client = NetworkLifecycle.Instance?.Client;
            if (clock == null || !ReferenceEquals(client, clockOwner))
            {
                clockOwner = client;
                clock = new PlayoutClock(TICK_SECONDS, MIN_DELAY_SECONDS, Math.Max(MIN_DELAY_SECONDS, Multiplayer.Settings.MaxPortPlayoutDelayMs / 1000.0));
            }
            return clock;
        }
    }

    private sealed class Track
    {
        public readonly Port Port;
        public readonly PlayoutTrack Values;
        public float Written = float.NaN;
        // Something on this client writes the port itself (a feeder, a controller): playing the
        // host's values out would fight it, so they're shown as they come, as before.
        public bool LocallyFed;
        public bool Active;

        public Track(Port port, PlayoutBlend blend)
        {
            Port = port;
            Values = new PlayoutTrack(blend);
        }
    }

    private abstract class Predictor
    {
        public readonly List<Port> Owned = [];
        public virtual void Activate() { }
        public abstract void Tick(float delta);
    }

    /// <summary>A vanilla component whose outputs depend only on the ports it reads: its own Tick runs here.</summary>
    private sealed class ComponentPredictor : Predictor
    {
        private readonly SimComponent component;
        private readonly Action activate;

        public ComponentPredictor(SimComponent component, Action activate = null)
        {
            this.component = component;
            this.activate = activate;
            foreach (Port port in component.GetAllPorts())
                if (port.type == PortType.READONLY_OUT || port.type == PortType.OUT)
                    Owned.Add(port);
        }

        public override void Activate() => activate?.Invoke();

        public override void Tick(float delta)
        {
            SimComponent_Tick_Patch.LocalTick = true;
            try
            {
                component.Tick(delta);
            }
            finally
            {
                SimComponent_Tick_Patch.LocalTick = false;
            }
        }
    }

    /// <summary>SteamExhaust.Tick's whistle, the rest of which is the host's (draught, steam use).</summary>
    private sealed class WhistlePredictor : Predictor
    {
        private readonly SteamExhaust exhaust;

        public WhistlePredictor(SteamExhaust exhaust)
        {
            this.exhaust = exhaust;
            Owned.Add(exhaust.whistleFlowNormalizedReadOut);
        }

        public override void Tick(float delta) =>
            exhaust.whistleFlowNormalizedReadOut.Value =
                Mathf.Min(Mathf.InverseLerp(1f, 2f, exhaust.boilerPressure.Value), exhaust.whistleControl.Value);
    }

    /// <summary>Sander.Tick's sand flow, without taking the sand (the host's).</summary>
    private sealed class SanderPredictor : Predictor
    {
        private readonly Sander sander;

        public SanderPredictor(Sander sander)
        {
            this.sander = sander;
            Owned.Add(sander.sandFlowReadOut);
            Owned.Add(sander.sandCoefReadOut);
        }

        public override void Activate()
        {
            sander.sandFlow = sander.sandFlowReadOut.Value;
            sander.sandFlowVelocity = 0f;
        }

        public override void Tick(float delta)
        {
            bool flowing = sander.sand.Value > 0f && (sander.powerFuseRef == null || sander.powerFuseRef.State);
            float target = flowing ? sander.controlExtIn.Value : 0f;
            sander.sandFlow = Mathf.SmoothDamp(sander.sandFlow, target, ref sander.sandFlowVelocity, 0.5f, float.PositiveInfinity, delta);
            if (sander.sandFlow < 0.001f && target == 0f)
                sander.sandFlow = 0f;
            sander.sandCoefReadOut.Value = Mathf.Lerp(1f, sander.sandCoeficientMax, sander.sandFlow);
            sander.sandFlowReadOut.Value = sander.sandFlow;
        }
    }

    private readonly NetworkedTrainCar car;
    private readonly SimulationFlow flow;
    private readonly Dictionary<Port, Track> tracks = [];
    private readonly List<Track> active = [];
    private readonly List<Predictor> predictors = [];
    private readonly HashSet<Port> predicted = [];
    private readonly PlayoutDelay delay = new();
    private readonly Action<float> writePassedStep;
    private Track writingTrack;
    private int lastPlayoutFrame = -1;
    private double lastPlayoutTime;
    private bool predicting;
    private bool failed;

    private TrainPortPresenter(NetworkedTrainCar car, SimulationFlow flow, Func<string, ControlPortWriter> classify)
    {
        this.car = car;
        this.flow = flow;
        writePassedStep = value => Write(writingTrack, value);

        foreach (SimComponent component in flow.OrderedSimComps)
        {
            Predictor predictor = component switch
            {
                Horn or SteamBell or ElectricBell or Dynamo or SmoothedOutput or Reverser => new ComponentPredictor(component),
                // Its smoothed value is kept in the component, not the port, and nothing updated it here.
                GenericControl control => new ComponentPredictor(control, () =>
                {
                    control.smoothedControl = control.controlReadOut.Value;
                    control.smoothedControlVelocity = 0f;
                }),
                SteamExhaust exhaust => new WhistlePredictor(exhaust),
                Sander sander => new SanderPredictor(sander),
                _ => null
            };
            if (predictor == null)
                continue;
            predictors.Add(predictor);
            predicted.UnionWith(predictor.Owned);
        }

        foreach (KeyValuePair<string, Port> kvp in flow.fullPortIdToPort)
        {
            Port port = kvp.Value;
            // Outputs of the simulation, which nothing else on a client writes. A control a
            // player drives is theirs, synced as it is.
            if (port.type != PortType.READONLY_OUT && port.type != PortType.OUT)
                continue;
            if (port.valueType == PortValueType.CONTROL && classify(kvp.Key) != ControlPortWriter.Simulation)
                continue;
            tracks[port] = new Track(port, BlendOf(port.valueType));
        }
    }

    private static PlayoutBlend BlendOf(PortValueType type) => type switch
    {
        // Typed STATE are many continuous values too (a bell's, a dynamo's or cylinder cocks'
        // flow, the crank's rotation), next to lamp states and pulses: blended, but a jump is a jump.
        PortValueType.GENERIC or PortValueType.CONTROL or PortValueType.STATE or PortValueType.DAMAGE => PlayoutBlend.Guarded,
        _ => PlayoutBlend.Continuous,
    };

    public static TrainPortPresenter Create(NetworkedTrainCar car, SimulationFlow flow, Func<string, ControlPortWriter> classify)
    {
        TrainPortPresenter presenter = new(car, flow, classify);
        byFlow[flow] = presenter;
        return presenter;
    }

    public void Destroy()
    {
        if (byFlow.TryGetValue(flow, out TrainPortPresenter presenter) && presenter == this)
            byFlow.Remove(flow);
    }

    /// <summary>Takes a value the host sent for one of this car's ports. Returns whether it's
    /// handled here (played out, or kept while predicted); if not, it's shown as it comes.</summary>
    public bool TakeHostValue(Port port, float value, uint hostTick)
    {
        if (failed || !tracks.TryGetValue(port, out Track track))
            return false;
        bool predictedNow = predicting && predicted.Contains(port);
        if (hostTick == 0)
        {
            // A resync: no tick to place it at, and nothing to blend from. Shown as it comes,
            // unless predicted.
            track.Values.Reset(Clock.Started ? Clock.HostTickAt(Now) : 0, value);
            if (!predictedNow)
                track.Written = float.NaN;
            return predictedNow;
        }

        if (track.LocallyFed || (!Multiplayer.Settings.SmoothTrainPorts && !predictedNow))
        {
            track.Values.Reset(hostTick, value);
            track.Written = float.NaN;
            return predictedNow;
        }
        track.Values.Add(hostTick, value);
        if (!predictedNow)
            Activate(track);
        return true;
    }

    private void Activate(Track track)
    {
        if (track.Active)
            return;
        track.Active = true;
        active.Add(track);
    }

    /// <summary>Called at the start of each of the car's simulation ticks on a client.</summary>
    public static void OnFlowTick(SimulationFlow flow, float delta)
    {
        if (!byFlow.TryGetValue(flow, out TrainPortPresenter presenter))
            return;
        try
        {
            presenter.Tick(delta);
        }
        catch (Exception e)
        {
            // Thrown into SimController.Update, it would stop the rest of the car's update. Show
            // the host's values as they come for this car from now on.
            Multiplayer.LogError($"TrainPortPresenter on {presenter.car.CurrentID} failed, showing the host's values as they come: {e}");
            presenter.failed = true;
            presenter.Destroy();
        }
    }

    private void Tick(float delta)
    {
        // A flow can tick several times a frame; the values shown change once.
        if (Time.frameCount != lastPlayoutFrame)
        {
            lastPlayoutFrame = Time.frameCount;
            bool driving = car.Client_IsLocalPlayerDrivingTrain(DRIVING_HOLD_SECONDS);
            UpdatePredicting(driving);
            Play(driving);
        }
        if (predicting)
            foreach (Predictor predictor in predictors)
                predictor.Tick(delta);
    }

    private void UpdatePredicting(bool driving)
    {
        bool wanted = driving && Multiplayer.Settings.PredictTrainPorts && predictors.Count > 0;
        if (wanted && !predicting)
            foreach (Predictor predictor in predictors)
                predictor.Activate();
        else if (!wanted && predicting)
            // What it predicted is shown until the host's values, played out, take over.
            foreach (Port port in predicted)
                if (tracks.TryGetValue(port, out Track track) && !track.Values.IsEmpty)
                {
                    track.Written = float.NaN;
                    Activate(track);
                }
        predicting = wanted;
    }

    private void Play(bool driving)
    {
        double now = Now;
        double elapsed = lastPlayoutTime == 0 ? 0 : now - lastPlayoutTime;
        lastPlayoutTime = now;
        if (!Clock.Started)
            return;
        delay.Step(driving || !Multiplayer.Settings.SmoothTrainPorts ? 0 : Clock.TargetDelaySeconds, elapsed);
        if (active.Count == 0)
            return;
        double shownTick = Clock.HostTickAt(now) - delay.Seconds / TICK_SECONDS;

        for (int i = active.Count - 1; i >= 0; i--)
        {
            Track track = active[i];
            if (predicting && predicted.Contains(track.Port))
            {
                // Keep its history moving on, without showing it.
                track.Values.ValueAt(shownTick);
                Deactivate(i, track);
                continue;
            }
            if (!float.IsNaN(track.Written) && track.Port.Value != track.Written)
            {
                track.LocallyFed = true;
                Multiplayer.LogDebug(() => $"TrainPortPresenter: {track.Port.id} on {car.CurrentID} is written on this client; showing the host's values as they come");
                Deactivate(i, track);
                continue;
            }

            writingTrack = track;
            float value = track.Values.ValueAt(shownTick, writePassedStep);
            Write(track, value);
            if (track.Values.SettledAt(shownTick))
                Deactivate(i, track);
        }
    }

    private void Deactivate(int index, Track track)
    {
        track.Active = false;
        int last = active.Count - 1;
        active[index] = active[last];
        active.RemoveAt(last);
    }

    private static void Write(Track track, float value)
    {
        track.Written = value;
        if (track.Port.Value == value)
            return;
        if (track.Port.type == PortType.EXTERNAL_IN)
            track.Port.ExternalValueUpdate(value);
        else
            track.Port.Value = value;
        // A port connected onward may round the value; compare against what it holds.
        track.Written = track.Port.Value;
    }
}
