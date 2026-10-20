using DV;
using DV.CabControls;
using DV.Customization.Paint;
using DV.Damage;
using DV.HUD;
using DV.Logic.Job;
using DV.MultipleUnit;
using DV.Simulation.Brake;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using DV.Simulation.Ports;
using DV.ThingTypes;
using JetBrains.Annotations;
using LocoSim.Definitions;
using LocoSim.Implementations;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Shared.Trains;

public class NetworkedTrainCar : IdMonoBehaviour<ushort, NetworkedTrainCar>
{
    private static int authoritativeTopologyApplicationDepth;
    private static int localTopologyBatchDepth;
    private float authoritativeTopologyObservationSuppressedUntil;
    internal static bool IsApplyingAuthoritativeTopology => authoritativeTopologyApplicationDepth > 0;
    internal static bool IsBatchingLocalTopology => localTopologyBatchDepth > 0;

    internal void SuppressAuthoritativeTopologyObservations(float seconds = 2f)
    {
        authoritativeTopologyObservationSuppressedUntil = Math.Max(
            authoritativeTopologyObservationSuppressedUntil,
            Time.realtimeSinceStartup + Math.Max(0f, seconds));
    }

    internal bool IsAuthoritativeTopologyObservationSuppressed =>
        IsApplyingAuthoritativeTopology ||
        Time.realtimeSinceStartup < authoritativeTopologyObservationSuppressedUntil;

    internal static IDisposable BeginAuthoritativeTopologyApplication()
    {
        authoritativeTopologyApplicationDepth++;
        return new AuthoritativeTopologyApplicationScope();
    }

    internal static IDisposable BeginLocalTopologyBatch()
    {
        localTopologyBatchDepth++;
        return new LocalTopologyBatchScope();
    }

    private sealed class AuthoritativeTopologyApplicationScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            authoritativeTopologyApplicationDepth = Math.Max(0,
                authoritativeTopologyApplicationDepth - 1);
        }
    }

    private sealed class LocalTopologyBatchScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            localTopologyBatchDepth = Math.Max(0, localTopologyBatchDepth - 1);
        }
    }

    #region Lookup Cache

    private static readonly Dictionary<TrainCar, NetworkedTrainCar> trainCarsToNetworkedTrainCars = [];
    private static readonly Dictionary<string, NetworkedTrainCar> trainCarIdToNetworkedTrainCars = [];
    private static readonly Dictionary<string, TrainCar> trainCarIdToTrainCars = [];
    private static readonly Dictionary<HoseAndCock, Coupler> hoseToCoupler = [];

    public static bool TryGet(ushort netId, out NetworkedTrainCar obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedTrainCar> rawObj);
        obj = (NetworkedTrainCar)rawObj;
        return b;
    }

    internal static bool TryGetQuiet(ushort netId, out NetworkedTrainCar obj)
    {
        bool found = Get(netId, out IdMonoBehaviour<ushort, NetworkedTrainCar> rawObj);
        obj = found ? (NetworkedTrainCar)rawObj : null;
        return found;
    }

    public static bool TryGet(ushort netId, out TrainCar trainCar)
    {
        bool b = TryGet(netId, out NetworkedTrainCar networkedTrainCar);
        trainCar = b ? networkedTrainCar.TrainCar : null;
        return b;
    }

    public static bool TryGet(ushort netId, out Car trainCar)
    {
        bool b = TryGet(netId, out NetworkedTrainCar networkedTrainCar);
        trainCar = b ? networkedTrainCar.TrainCar?.logicCar : null;
        return b;
    }

    public static bool TryGetCoupler(HoseAndCock hoseAndCock, out Coupler coupler)
    {
        return hoseToCoupler.TryGetValue(hoseAndCock, out coupler);
    }

    public static bool GetFromTrainId(string carId, out NetworkedTrainCar networkedTrainCar)
    {
        return trainCarIdToNetworkedTrainCars.TryGetValue(carId, out networkedTrainCar);
    }
    public static bool GetTrainCarFromTrainId(string carId, out TrainCar trainCar)
    {
        return trainCarIdToTrainCars.TryGetValue(carId, out trainCar);
    }

    public static bool TryGetFromTrainCar(TrainCar trainCar, out NetworkedTrainCar networkedTrainCar)
    {
        return trainCarsToNetworkedTrainCars.TryGetValue(trainCar, out networkedTrainCar);
    }

    /// <summary>
    /// Resolves a transform parented to a car's detached interior hierarchy. DV keeps some
    /// interiors outside the TrainCar transform hierarchy, so GetComponentInParent cannot be
    /// relied on for loose cab items.
    /// </summary>
    public static bool TryGetFromInteriorHierarchy(Transform candidate, out TrainCar trainCar)
    {
        trainCar = null;
        if (candidate == null)
            return false;

        foreach (TrainCar registeredCar in trainCarsToNetworkedTrainCars.Keys)
        {
            if (registeredCar == null)
                continue;
            Transform interior = registeredCar.interior ?? registeredCar.transform;
            if (candidate == interior || candidate.IsChildOf(interior))
            {
                trainCar = registeredCar;
                return true;
            }
        }
        return false;
    }

    public static bool TryGetNetId(TrainCar trainCar, out ushort netId)
    {
        netId = 0;

        if (!trainCarsToNetworkedTrainCars.TryGetValue(trainCar, out var networkedTrainCar) || networkedTrainCar == false || networkedTrainCar.NetId == 0)
            return false;

        netId = networkedTrainCar.NetId;
        return true;
    }

    public static bool TryGetNetId(Car car, out ushort netId)
    {
        netId = 0;

        if (car == null || !GetFromTrainId(car.ID, out var networkedTrainCar) || networkedTrainCar == false || networkedTrainCar.NetId == 0)
            return false;

        netId = networkedTrainCar.NetId;
        return true;
    }

    internal static NetworkedTrainCar[] GetAllRegistered() =>
        trainCarsToNetworkedTrainCars.Values
            .Where(value => value != null && value.TrainCar != null && value.NetId != 0)
            .Distinct()
            .ToArray();

    #endregion
    private const int MAX_COUPLER_ITERATIONS = 50;
    private const float MAX_PORT_DELTA = 0.001f;
    #region Port and Fuse Map

    private static readonly Dictionary<uint, string> netIdToPort = [];
    private static readonly Dictionary<string, uint> portToNetId = [];
    private static readonly Dictionary<uint, string> netIdToFuse = [];
    private static readonly Dictionary<string, uint> fuseToNetId = [];

    internal static uint GetPortNetId(string portId)
    {
        if (portToNetId.TryGetValue(portId, out var netId))
            return netId;

        netId = StringHashing.Fnv1aHash(portId);

        Multiplayer.LogDebug(() => $"GetPortNetId({portId}) Registering with {netId}");

        netIdToPort[netId] = portId;
        portToNetId[portId] = netId;

        return netId;
    }

    internal static string GetPort(uint netId)
    {
        netIdToPort.TryGetValue(netId, out var portId);
        return portId;
    }

    internal static uint GetFuseNetId(string fuseId)
    {
        if (fuseToNetId.TryGetValue(fuseId, out var netId))
            return netId;

        netId = StringHashing.Fnv1aHash(fuseId);
        netIdToFuse[netId] = fuseId;
        fuseToNetId[fuseId] = netId;

        return netId;
    }
    internal static string GetFuse(uint netId)
    {
        netIdToFuse.TryGetValue(netId, out var portId);
        return portId;
    }
    #endregion

    public float CarLengthSq => CarSpawner.Instance.carLiveryToCarLength[TrainCar.carLivery] * CarSpawner.Instance.carLiveryToCarLength[TrainCar.carLivery];

    public string CurrentID { get; private set; }
    public TrainCar TrainCar;

    // Why each end last came uncoupled and why the car last derailed (see TrainChangeCauses).
    public ChangeCause<UncoupleReason>? LastFrontUncouple { get; internal set; }
    public ChangeCause<UncoupleReason>? LastRearUncouple { get; internal set; }
    public ChangeCause<DerailReason>? LastDerail { get; internal set; }
    public uint TicksSinceSync = uint.MaxValue;

    public uint lastTickProcessed = 0;
    public uint ControlRevision { get; internal set; }
    public bool HasPlayers => PlayerManager.Car == TrainCar || GetComponentInChildren<NetworkedPlayer>() != null;

    internal Bogie bogie1;
    internal Bogie bogie2;
    internal BrakeSystem brakeSystem;

    internal bool hasSimFlow;
    internal SimulationFlow simulationFlow;
    SimController simController;
    public FireboxSimController firebox;
    // The compressor's activation signal: typed CONTROL, but the brake system sets it.
    private readonly HashSet<uint> compressorSignalPorts = new();
    internal Port compressorSignalPort;
    // Ports vanilla writes every frame on every game from that game's own car: TractionPortsFeeder's
    // forward speed, wheel rpm and wheel speed, and a steam boiler's tilt (BoilerSimController).
    // Each game feeds its own, so they aren't synced. Applying the host's too gave them two
    // writers, and a speedometer flicked between the two values.
    internal readonly HashSet<uint> locallyFedPorts = new();
    // A handcar's handle ports: its position (BAR) and whether it's held (ENGAGED). Each game's are
    // its own player's handle, so they take no lease and any number of players can pump at once.
    // The host keeps other players' handles apart from its ports (NetworkedTrainCarServer), and no
    // game applies another game's.
    internal readonly HashSet<uint> handcarHandlePorts = new();
    internal uint handcarBarPortNetId;
    internal uint handcarEngagedPortNetId;
    internal HandcarDrive handcarDrive;
    // Which CONTROL ports players drive: the only ones a client sends, or the host takes from one.
    internal ControlPortWriters<uint> portWriters;
    public CoalPileSimController coalPile;

    internal HashSet<uint> dirtyPorts;
    private Dictionary<uint, float> lastSentPortValues;
    internal HashSet<uint> dirtyFuses;
    private readonly HashSet<TrainCarPaint> dirtyPaints = [];

    private Dictionary<InteriorControlsManager.ControlType, OverridableBaseControl> controlTypeToControl = [];
    private readonly Dictionary<uint, OverridableBaseControl> portToBaseControl = [];
    internal bool handbrakeDirty;

    // A handcar's handbrake springs off unless its own game's player holds it (vanilla
    // HandcarHandbrakeController, on every game). Handbrakes take no lease, so the handbrake sync
    // stands in for one: a game that has seen another player's handbrake change recently leaves
    // the spring-back alone, and a player holding it resends it so that stays true while it's held.
    private const float REMOTE_HANDBRAKE_HOLD_SECONDS = 1f;
    private const float HANDBRAKE_HOLD_RESEND_SECONDS = 0.25f;
    private float remoteHandbrakeChangedAt = float.NegativeInfinity;
    private float lastHandbrakeSentAt = float.NegativeInfinity;

    /// <summary>Another player changed or is holding this car's handbrake.</summary>
    internal bool HandbrakeHeldRemotely =>
        Time.realtimeSinceStartup - remoteHandbrakeChangedAt < REMOTE_HANDBRAKE_HOLD_SECONDS;

    /// <summary>A handbrake change from another player reached this game (the host from a client's
    /// request, a client from another player's commit).</summary>
    internal void Common_OnRemoteHandbrakeChange() => remoteHandbrakeChangedAt = Time.realtimeSinceStartup;

    /// <summary>This game's player is holding the handbrake on: keep the other games told.</summary>
    internal void Common_HoldingHandbrake()
    {
        if (Time.realtimeSinceStartup - lastHandbrakeSentAt >= HANDBRAKE_HOLD_RESEND_SECONDS)
            handbrakeDirty = true;
    }

    public bool BogieTracksDirty;
    public byte CargoModelIndex = byte.MaxValue;

    public bool IsDestroying;

    #region The local player's chain interaction

    // The car's followers are set up (on a client, once its bogies have theirs; the host has none).
    public bool Client_Initialized { get; internal set; }

    // The local player's chain interaction (NetworkedTrainCarClient), which a coupler interaction
    // from the host is checked against (Common_ReceiveCouplerInteraction).
    internal Coupler couplerInteraction;

    // Chains the local player is dragging right now. Vanilla uncouples locally when a coupled chain
    // is picked up; the host only hears about it on the drop. A topology snapshot applied mid-drag
    // re-coupled the cars under the chain, and the drop then went unreported. Snapshots wait while a
    // drag is in progress (checked live, so a destroyed chain can never block them).
    internal static readonly HashSet<Coupler> localChainDrags = new();

    internal static bool IsLocalChainDragInProgress
    {
        get
        {
            localChainDrags.RemoveWhere(coupler => coupler == null || coupler.ChainScript == null ||
                coupler.ChainScript.CurrentState != ChainCouplerInteraction.State.Being_Dragged);
            return localChainDrags.Count > 0;
        }
    }
    internal ChainCouplerInteraction.State originalState;
    internal Coupler originalCoupledTo;

    #endregion

    /// <summary>
    /// The server's half of this car (Server/Trains/NetworkedTrainCarServer.cs), on the server.
    /// This component drives it at the points its code always ran: the end of Start, OnDisable,
    /// each tick after the car's own work, and the host's own control changes.
    /// </summary>
    internal ITrainCarServerPart ServerPart { get; private set; }

    /// <summary>
    /// The local player's half of this car (Client/Trains/NetworkedTrainCarClient.cs), on every game
    /// someone plays at, the host's too. Driven the same way: the end of Start, OnDisable, the
    /// player's own control changes, and the host's values arriving.
    /// </summary>
    internal ITrainCarClientPart ClientPart { get; private set; }

    /// <summary>
    /// This game's half for the car's own changes (<see cref="ITrainCarRole"/>): the server's on the
    /// server, the local player's on a client; null when neither was made.
    /// </summary>
    internal ITrainCarRole Role { get; private set; }

    protected override bool IsIdServerAuthoritative => true;

    protected override void Awake()
    {
        base.Awake();

        TrainCar = GetComponent<TrainCar>();
        trainCarsToNetworkedTrainCars[TrainCar] = this;
        EntityDebugRegistry.RegisterTrain(this);
        DebugRuntime.Publish("train", "train.registered", NetworkRole.DebugSide,
            entityType: "TrainCar", entityId: NetId.ToString(), data: DebugValueSnapshotter.SnapshotObject(new { CurrentID, NetId }));

        TrainCar.LogicCarInitialized += OnLogicCarInitialised;

        bogie1 = TrainCar.Bogies[0];
        bogie2 = TrainCar.Bogies[1];

        ServerPart = TrainRoles.Server?.CreateCarPart(this);
        ClientPart = TrainRoles.Client?.CreateCarPart(this);
        Role = (ITrainCarRole)ServerPart ?? ClientPart;
    }

    [UsedImplicitly]
    protected void Start()
    {
        float time = Time.time;

        if (TrainCar == null)
        {
            Multiplayer.LogError($"NetworkedTrainCar.Start() TrainCar is null on {gameObject?.name} after {(Time.time - time):F2}s");
            return;
        }

        Multiplayer.LogDebug(() => $"NetworkedTrainCar.Start({TrainCar?.ID}, {NetId})");

        if (TrainCar.couplers == null || TrainCar.couplers.Length == 0)
        {
            Multiplayer.LogWarning($"NetworkedTrainCar.Start() Couplers are null or empty on {CurrentID}");
        }
        else
        {
            foreach (Coupler coupler in TrainCar.couplers)
            {
                hoseToCoupler[coupler.hoseAndCock] = coupler;

                //Multiplayer.LogDebug(() => $"TrainCar.Start() [{TrainCar?.ID}, {NetId}], Coupler exists: {coupler != null}, Is front: {coupler.isFrontCoupler}, ChainScript exists: {coupler.ChainScript != null}");
            }

            Multiplayer.LogDebug(() => $"NetworkedTrainCar.Start({CurrentID}, {NetId}) Couplers complete");
        }

        simController = GetComponent<SimController>();
        if (simController != null)
        {
            hasSimFlow = true;
            simulationFlow = simController.SimulationFlow;

            // Find all train controls
            if (simController.controlsOverrider == null)
            {
                Multiplayer.LogWarning($"NetworkedTrainCar.Start() ControlsOverrider is null on car {CurrentID}");
            }
            else
            {
                controlTypeToControl = simController.controlsOverrider.controlsMap;
                foreach (var kvp in controlTypeToControl)
                {
                    var control = kvp.Value;
                    if (control == null)
                    {
                        Multiplayer.LogWarning($"NetworkedTrainCar.Start() Control {kvp.Key} is null on car {CurrentID}");
                        continue;
                    }

                    if (string.IsNullOrEmpty(control.portId))
                    {
                        Multiplayer.LogWarning($"NetworkedTrainCar.Start() Control {kvp.Key} has no portId on car {CurrentID}");
                        continue;
                    }

                    var portNetId = GetPortNetId(control.portId);
                    portToBaseControl[portNetId] = control;
                    if (DebugRuntime.EnabledFor("train"))
                        EntityDebugRegistry.Register("Control/Port", $"{NetId}:{portNetId}", $"{CurrentID} {kvp.Key}", this,
                            DebugValueSnapshotter.SnapshotObject(new { carNetId = NetId, portNetId, portId = control.portId, controlType = kvp.Key.ToString() }));

                    Multiplayer.LogDebug(() => $"NetworkedTrainCar.Start() Control {kvp.Key} has portId {control.portId} on car {CurrentID}");
                }
            }

            if (simulationFlow != null)
            {
                foreach (CompressorSimController compressor in TrainCar.GetComponentsInChildren<CompressorSimController>(true))
                    if (!string.IsNullOrEmpty(compressor.activationSignalExtInPortId) &&
                        simulationFlow.TryGetPort(compressor.activationSignalExtInPortId, out Port signal))
                    {
                        compressorSignalPorts.Add(GetPortNetId(compressor.activationSignalExtInPortId));
                        compressorSignalPort = signal;
                    }
                portWriters = new ControlPortWriters<uint>(
                    simulationFlow.fullPortIdToPort.Where(kvp => kvp.Value.valueType == PortValueType.CONTROL).Select(kvp => GetPortNetId(kvp.Key)),
                    InteractablePortScan.PortsOf(TrainCar.carLivery).Select(GetPortNetId),
                    compressorSignalPorts);
                Multiplayer.LogDebug(() => $"NetworkedTrainCar.Start({CurrentID}, {NetId}) {portWriters.PlayerPortCount} player-driven control ports");
            }

            if (simulationFlow != null)
            {
                if (simController.tractionPortsFeeder is { } feeder)
                    foreach (string portId in new[] { feeder.forwardSpeedPortId, feeder.wheelRpmPortId, feeder.wheelSpeedKmhPortId })
                        if (!string.IsNullOrEmpty(portId))
                            locallyFedPorts.Add(GetPortNetId(portId));
                foreach (BoilerSimController boiler in TrainCar.GetComponentsInChildren<BoilerSimController>(true))
                    if (!string.IsNullOrEmpty(boiler.anglePortId))
                        locallyFedPorts.Add(GetPortNetId(boiler.anglePortId));

                handcarDrive = simulationFlow.OrderedSimComps.OfType<HandcarDrive>().FirstOrDefault();
                if (handcarDrive != null)
                {
                    handcarBarPortNetId = GetPortNetId(handcarDrive.handcarBarExtIn.id);
                    handcarEngagedPortNetId = GetPortNetId(handcarDrive.handleEngagedExtIn.id);
                    handcarHandlePorts.Add(handcarBarPortNetId);
                    handcarHandlePorts.Add(handcarEngagedPortNetId);
                }

                dirtyPorts =new HashSet<uint>(simulationFlow.fullPortIdToPort.Count);
                lastSentPortValues = new Dictionary<uint, float>(dirtyPorts.Count);
                foreach (KeyValuePair<string, Port> kvp in simulationFlow.fullPortIdToPort)
                {
                    var portNetId = GetPortNetId(kvp.Key);
                    if (locallyFedPorts.Contains(portNetId))
                        continue;
                    if (kvp.Value.valueType == PortValueType.CONTROL || Role?.Reports(kvp.Value, portNetId) == true)
                    {
                        if (portToBaseControl.TryGetValue(portNetId, out var control))
                        {
                            Multiplayer.LogDebug(() => $"NetworkedTrainCar.Start({CurrentID}, {NetId}) Subscribing to control {control.ControlType} for {kvp.Key} with netId {portNetId}");
                            control.ControlUpdated += _ => { Common_OnPortUpdated(kvp.Value, portNetId); };
                        }
                        else
                        {
                            Multiplayer.LogDebug(() => $"NetworkedTrainCar.Start({CurrentID}, {NetId}) Subscribing to port {kvp.Key}");
                            kvp.Value.ValueUpdatedInternally += _ => { Common_OnPortUpdated(kvp.Value, portNetId); };
                        }
                    }
                }

                dirtyFuses = new HashSet<uint>(simulationFlow.fullFuseIdToFuse.Count);
                foreach (KeyValuePair<string, Fuse> kvp in simulationFlow.fullFuseIdToFuse)
                {
                    var fuseNetId = GetFuseNetId(kvp.Key);
                    kvp.Value.StateUpdated += _ => { Common_OnFuseUpdated(fuseNetId); };
                }
            }
            else
            {
                Multiplayer.LogWarning($"NetworkedTrainCar.Start() SimulationFlow is null on {CurrentID}");
            }

            firebox = simController.firebox;
            coalPile = simController.coalPile;
        }

        Multiplayer.LogDebug(() => $"NetworkedTrainCar.Start({TrainCar?.ID}, {NetId}) SimController complete");

        if (TrainCar.brakeSystem != null)
        {
            brakeSystem = TrainCar.brakeSystem;
            brakeSystem.HandbrakePositionChanged += Common_OnHandbrakePositionChanged;
            brakeSystem.BrakeCylinderReleased += Common_OnBrakeCylinderReleased;
        }
        else
        {
            Multiplayer.LogError($"NetworkedTrainCar.Start() BrakeSystem is null on {CurrentID}");
        }

        if (TrainCar.PaintExterior != null)
            TrainCar.PaintExterior.OnThemeChanged += Common_OnPaintThemeChange;
        if (TrainCar.PaintInterior != null)
            TrainCar.PaintInterior.OnThemeChanged += Common_OnPaintThemeChange;

        NetworkLifecycle.Instance.OnTick += OnTick;

        ServerPart?.Start();
        ClientPart?.Start();
    }

    /// <summary>
    /// Set when the car is deleted (or returned to DV's car pool). The component destroys itself
    /// then, but Unity only does that at the end of the frame; see <see cref="OnDisable"/>.
    /// </summary>
    internal bool Retired { get; private set; }

    public void OnDisable()
    {
        ServerPart?.Unregister();

        if (UnloadWatcher.isQuitting || Retired)
            return;

        DebugRuntime.Publish("train", "train.unregistered", NetworkRole.DebugSide,
            entityType: "TrainCar", entityId: NetId.ToString(), data: DebugValueSnapshotter.SnapshotObject(new { CurrentID, NetId }));
        EntityDebugRegistry.Unregister("TrainCar", NetId.ToString());

        //Clean dictionaries
        ClientPart?.Disable();
        trainCarsToNetworkedTrainCars.Remove(TrainCar);
        trainCarIdToNetworkedTrainCars.Remove(CurrentID);
        trainCarIdToTrainCars.Remove(CurrentID);

        foreach (Coupler coupler in TrainCar.couplers)
            hoseToCoupler.Remove(coupler.hoseAndCock);

        //stop tracking client events
        NetworkLifecycle.Instance.OnTick -= OnTick;

        if (brakeSystem != null)
        {
            brakeSystem.HandbrakePositionChanged -= Common_OnHandbrakePositionChanged;
            brakeSystem.BrakeCylinderReleased -= Common_OnBrakeCylinderReleased;
        }

        if (TrainCar.PaintExterior != null)
            TrainCar.PaintExterior.OnThemeChanged -= Common_OnPaintThemeChange;
        if (TrainCar.PaintInterior != null)
            TrainCar.PaintInterior.OnThemeChanged -= Common_OnPaintThemeChange;

        //stop tracking server events
        ServerPart?.Disable();

        CurrentID = string.Empty;
        // Destroy only takes effect at the end of the frame, and DV's car pool can hand this
        // same object to a new car before then: a delete and a spawn of the same livery in one
        // packet batch, or a host deleting and spawning in one frame. Free the ID now, so the
        // new car's can't collide with it, and mark the component retired so the pool hand-out
        // replaces it instead of keeping it (TrainCarPatch). Kept, it was destroyed at the end of
        // the frame and left the new car with no network identity, which blocked every topology
        // snapshot on the client from then on (#69).
        Retired = true;
        ReleaseNetId();
        Destroy(this);
    }

    #region Server

    private void OnLogicCarInitialised()
    {
        //Multiplayer.LogWarning("OnLogicCarInitialised");
        if (TrainCar.logicCar != null)
        {
            CurrentID = TrainCar.ID;
            trainCarIdToNetworkedTrainCars[CurrentID] = this;
            trainCarIdToTrainCars[CurrentID] = TrainCar;

            TrainCar.LogicCarInitialized -= OnLogicCarInitialised;
        }
        else
        {
            Multiplayer.LogWarning("OnLogicCarInitialised Car Not Initialised!");
        }

    }
    public bool TryCapturePortValues(uint[] portNetIds, out float[] values)
    {
        values = null;
        if (!hasSimFlow || portNetIds == null)
            return false;
        values = new float[portNetIds.Length];
        for (int index = 0; index < portNetIds.Length; index++)
        {
            string portId = GetPort(portNetIds[index]);
            if (portId == null || !simulationFlow.TryGetPort(portId, out Port port))
                return false;
            values[index] = port.Value;
        }
        return true;
    }

    public bool TryCaptureFuseValues(uint[] fuseNetIds, out bool[] values)
    {
        values = null;
        if (!hasSimFlow || fuseNetIds == null)
            return false;
        values = new bool[fuseNetIds.Length];
        for (int index = 0; index < fuseNetIds.Length; index++)
        {
            string fuseId = GetFuse(fuseNetIds[index]);
            if (fuseId == null || !simulationFlow.TryGetFuse(fuseId, out Fuse fuse))
                return false;
            values[index] = fuse.State;
        }
        return true;
    }

    public TrainControlStateData CaptureControlState()
    {
        uint[] portIds = hasSimFlow
            ? simulationFlow.fullPortIdToPort
                .Where(pair => pair.Value.valueType == PortValueType.CONTROL)
                .Select(pair => GetPortNetId(pair.Key)).Distinct().OrderBy(value => value).ToArray()
            : Array.Empty<uint>();
        TryCapturePortValues(portIds, out float[] portValues);
        uint[] fuseIds = hasSimFlow
            ? simulationFlow.fullFuseIdToFuse.Keys.Select(GetFuseNetId)
                .Distinct().OrderBy(value => value).ToArray()
            : Array.Empty<uint>();
        TryCaptureFuseValues(fuseIds, out bool[] fuseValues);
        return new TrainControlStateData
        {
            CarNetId = NetId,
            Revision = ControlRevision,
            PortIds = portIds,
            PortValues = portValues ?? Array.Empty<float>(),
            FuseIds = fuseIds,
            FuseValues = fuseValues ?? Array.Empty<bool>(),
            HasHandbrake = brakeSystem?.hasHandbrake == true,
            HandbrakePosition = brakeSystem?.handbrakePosition ?? 0f
        };
    }

    #endregion

    #region Common

    // The car's tick: its own sends, then the server's half, as when each was subscribed in turn.
    private void OnTick(uint tick)
    {
        Common_OnTick(tick);
        ServerPart?.Tick(tick);
    }

    private void Common_OnTick(uint tick)
    {
        if (UnloadWatcher.isUnloading)
            return;

        Common_SendHandbrakePosition();
        Common_SendFuses();
        Common_SendPorts();
        Common_SendPaintThemes();
    }

    private void Common_SendHandbrakePosition() => Common_SendHandbrakePosition(viaRemote: false);

    /// <summary>Sends a pending handbrake change now, marked as made by a remote controller (vanilla
    /// sets handbrakes when coupling by remote, with the player possibly far from the cars).</summary>
    internal void Client_FlushHandbrakeViaRemote() => Common_SendHandbrakePosition(viaRemote: true);

    private void Common_SendHandbrakePosition(bool viaRemote)
    {
        if (!handbrakeDirty)
            return;
        if (!TrainCar.brakeSystem.hasHandbrake)
            return;

        handbrakeDirty = false;
        if (Role == null || !Role.SendsChange(viaRemote))
            return; // simulation noise (e.g. set during load), not a player action
        Role.SendHandbrake(viaRemote);
        lastHandbrakeSentAt = Time.realtimeSinceStartup;
    }

    public void Common_DirtyPorts(uint[] portNetIds)
    {
        if (!hasSimFlow)
            return;

        foreach (uint portNetId in portNetIds)
        {
            var portId = GetPort(portNetId);
            if (!simulationFlow.TryGetPort(portId, out Port _))
            {
                Multiplayer.LogWarning($"Tried to dirty port [{portId}, {portNetId}] on {CurrentID} but port doesn't exist!");
                continue;
            }

            dirtyPorts.Add(portNetId);
        }
    }

    public void Common_DirtyFuses(uint[] fuseNetIds)
    {
        if (!hasSimFlow)
            return;

        foreach (uint fuseNetId in fuseNetIds)
        {
            var fuseId = GetFuse(fuseNetId);
            if (!simulationFlow.TryGetFuse(fuseId, out Fuse _))
            {
                Multiplayer.LogWarning($"Tried to dirty port [{fuseId}, {fuseNetId}] on {CurrentID} but it doesn't exist!");
                continue;
            }

            dirtyFuses.Add(fuseNetId);
        }
    }

    internal void Common_SendPorts()
    {
        if (!hasSimFlow || dirtyPorts.Count == 0)
            return;

        List<uint> settling = null;
        uint[] portIds = Role?.PortsToSend(dirtyPorts, out settling) ?? Array.Empty<uint>();
        // Other unowned dirty controls are native simulation feedback, not client intent.
        // Retaining them would either spin forever or contaminate a later leased batch.
        dirtyPorts.Clear();
        // A change made while the host's last value is still settling waits for it to settle,
        // then goes if it still differs. Dropping it lost a player's change for good whenever it
        // fell in that window: on a car resynced every few seconds (each game loading its interior
        // asks for a full resync), closed oiling caps reopened at the next resync (playtest 2).
        if (settling != null)
            foreach (uint id in settling)
                dirtyPorts.Add(id);
        if (portIds.Length == 0)
            return;

        int i = 0;
        float[] portValues = new float[portIds.Length];
        foreach (uint portNetId in portIds)
        {
            var portId = GetPort(portNetId);
            if (simulationFlow.TryGetPort(portId, out Port port))
            {
                float value = port.Value;
                portValues[i] = value;
                lastSentPortValues[portNetId] = value;
            }
            else
            {
                Multiplayer.LogWarning($"Failed to send port \"{portId}\" for [{CurrentID}, {NetId}]");
            }

            i++;
        }

        Role?.SendPorts(portIds, portValues);
    }

    private void Common_SendFuses()
    {
        if (!hasSimFlow || dirtyFuses.Count == 0)
            return;
        if (Role?.SendsChange(viaRemote: false) != true)
        {
            dirtyFuses.Clear(); // simulation noise (e.g. set during load), not a player action
            return;
        }

        int i = 0;
        uint[] fuseIds = dirtyFuses.ToArray();
        bool[] fuseValues = new bool[fuseIds.Length];

        foreach (uint fuseNetId in dirtyFuses)
        {
            var fuseId = GetFuse(fuseNetId);
            if (simulationFlow.TryGetFuse(fuseId, out Fuse fuse))
                fuseValues[i] = fuse.State;
            else
                Multiplayer.LogWarning($"Failed to send fuse \"{fuseId}\" for [{CurrentID}, {NetId}]");

            i++;
        }

        dirtyFuses.Clear();
        Role?.SendFuses(fuseIds, fuseValues);
    }

    private void Common_SendPaintThemes()
    {
        if (dirtyPaints.Count == 0)
            return;

        //Multiplayer.LogDebug(() => $"Common_SendPaintThemes() target: {paintController.TargetArea}, theme: {paintController.CurrentTheme.name}");
        foreach (var paintController in dirtyPaints)
        {
            if (!PaintThemeLookup.Instance.TryGetNetId(paintController.CurrentTheme, out var themeNetId))
            {
                Multiplayer.LogWarning($"Common_SendPaintThemes() could not find themeId for theme {paintController.CurrentTheme.name} on [{CurrentID}, {NetId}]");
                return;
            }

            Role?.SendPaintTheme(paintController.targetArea, themeNetId);
        }

        dirtyPaints.Clear();
    }

    private void Common_OnHandbrakePositionChanged((float, bool) data)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        handbrakeDirty = true;
    }

    private void Common_OnBrakeCylinderReleased()
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        Role?.SendBrakeCylinderReleased();
    }

    private void Common_OnPortUpdated(Port port, uint portNetId)
    {
        DebugRuntime.PublishHighFrequency("train", "train.port-value-changed", NetworkRole.DebugSide,
            "Control/Port", $"{NetId}:{portNetId}",
            () => DebugValueSnapshotter.SnapshotObject(new { carNetId = NetId, portNetId, port.id, port.Value, port.prevValue, port.valueType }));
        //Multiplayer.LogDebug(() => $"Common_OnPortUpdated() port [{port?.id}] updated on [{CurrentID}, {NetId}]. Value: {port?.Value}, PrevValue: {port?.prevValue}, ValueType: {port?.valueType}, Tick: {NetworkLifecycle.Instance.Tick}");
        if (Role?.Reports(port, portNetId) != true)
            return;

        // A host value being applied, as it lands or played out (TrainPortPresenter), isn't a change here.
        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket || ClientPart?.WritingHostValues == true)
            return;
        if (float.IsNaN(port.prevValue) && float.IsNaN(port.Value))
            return;
        Role?.NoteLocalControlChange();

        bool hasLastSent = lastSentPortValues.TryGetValue(portNetId, out float lastSentValue);
        float delta = Mathf.Abs(lastSentValue - port.Value);

        if (port.valueType == PortValueType.STATE)
        {
            if (!hasLastSent || lastSentValue != port.Value)
            {
                dirtyPorts.Add(portNetId);
            }
        }
        else
        {
            if (!hasLastSent || delta > MAX_PORT_DELTA || (port.Value == 0 && lastSentValue != 0))
            {
                dirtyPorts.Add(portNetId);
            }

            if (port.valueType == PortValueType.CONTROL)
            {
                if (ClientPart?.IsBlocked(portNetId) == true)
                {
                    Multiplayer.LogDebug(() => $"Common_OnPortUpdated() Control port [{port.id}, {portNetId}] is currently blocked on [{CurrentID}, {NetId}]");
                    return;
                }
                dirtyPorts.Add(portNetId);
            }
        }
    }

    private void Common_OnPaintThemeChange(TrainCarPaint paintController)
    {
        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        if (paintController == null)
            return;

        dirtyPaints.Add(paintController);
    }

    private void Common_OnFuseUpdated(uint fuseNetId)
    {
        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        dirtyFuses.Add(fuseNetId);
    }

    /// <param name="hostTick">On a client, the host's tick the values are from (0 for a resync):
    /// the simulation's outputs are played out on it rather than shown as they land.</param>
    public void Common_UpdatePorts(CommonTrainPortsPacket packet, uint hostTick = 0)
    {
        if (!hasSimFlow)
            return;

        for (int i = 0; i < packet.PortIds.Length; i++)
        {
            if (locallyFedPorts.Contains(packet.PortIds[i])) // an older host still sends them
                continue;
            // Each game's handle is its own player's (a commit carries the host's own).
            if (handcarHandlePorts.Contains(packet.PortIds[i]))
                continue;
            var portId = GetPort(packet.PortIds[i]);
            if (simulationFlow.TryGetPort(portId, out Port port))
            {
                float value = packet.PortValues[i];
                if (Role?.TakeHostPortValue(packet.PortIds[i], port, value, hostTick) == true)
                    continue;

                if (port.type == PortType.EXTERNAL_IN)
                    port.ExternalValueUpdate(value);
                else
                    port.Value = value;
            }
            else
            {
                Multiplayer.LogWarning($"Failed to update port [\"portId\", {packet.PortIds[i]}] with value \"{packet.PortValues[i]}\" for [{CurrentID}, {NetId}]");
            }
        }
    }

    public void Common_UpdateFuses(CommonTrainFusesPacket packet)
    {
        if (!hasSimFlow)
            return;

        for (int i = 0; i < packet.FuseIds.Length; i++)
        {
            var fuseId = GetFuse(packet.FuseIds[i]);
            if (simulationFlow.TryGetFuse(fuseId, out Fuse fuse))
                fuse.ChangeState(packet.FuseValues[i]);
            else
                Multiplayer.LogWarning($"Failed to update fuse [\"fuseId\", {packet.FuseIds[i]}] with value \"{packet.FuseValues[i]}\" for [{CurrentID}, {NetId}]");
        }
    }

    public void Common_ReceiveCouplerInteraction(CommonCouplerInteractionPacket packet)
    {
        CouplerInteractionType flags = (CouplerInteractionType)packet.Flags;
        Coupler coupler = packet.IsFrontCoupler ? TrainCar?.frontCoupler : TrainCar?.rearCoupler;
        TrainCar otherCar = null;
        Coupler otherCoupler = null;

        ButtonBase buttonBase = coupler?.ChainScript?.screwButton.GetComponent<ButtonBase>();

        Multiplayer.LogDebug(() => $"Common_ReceiveCouplerInteraction() couplerNetId: {NetId}, coupler is front: {packet.IsFrontCoupler}, flags: {flags}, otherCouplerNetId: {packet.OtherNetId}, otherCoupler is front: {packet.IsFrontOtherCoupler}");

        if (coupler == null)
        {
            Multiplayer.LogWarning($"Common_ReceiveCouplerInteraction() did not find coupler for [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}");
            return;
        }

        if (packet.OtherNetId != 0)
        {
            if (TryGet(packet.OtherNetId, out otherCar))
                otherCoupler = packet.IsFrontOtherCoupler ? otherCar?.frontCoupler : otherCar?.rearCoupler;
        }

        Multiplayer.LogDebug(() => $"Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}, otherCouplerNetId: {packet.OtherNetId}");

        if (flags == CouplerInteractionType.NoAction)
        {
            Multiplayer.LogDebug(() => $"Common_ReceiveCouplerInteraction() Interaction rejected! [{CurrentID}, {NetId}]");
            //our interaction was denied
            coupler.ChainScript?.knobGizmo?.ForceEndInteraction();
            couplerInteraction = null;

            if (coupler.ChainScript.state == originalState)
                return;

            switch (originalState)
            {
                case ChainCouplerInteraction.State.Parked:
                    StartCoroutine(ParkCoupler(coupler));
                    break;
                case ChainCouplerInteraction.State.Dangling:
                    if (coupler.ChainScript.state == ChainCouplerInteraction.State.Attached_Tight)
                        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Screw_Used);

                    StartCoroutine(DangleCoupler(coupler));
                    break;
                case ChainCouplerInteraction.State.Attached_Loose:
                    if (coupler.ChainScript.state == ChainCouplerInteraction.State.Attached_Tight)
                        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Screw_Used);
                    else
                        StartCoroutine(LooseAttachCoupler(coupler, originalCoupledTo));
                    break;
                case ChainCouplerInteraction.State.Attached_Tight:
                    if (coupler.ChainScript.state != ChainCouplerInteraction.State.Attached_Loose)
                        StartCoroutine(LooseAttachCoupler(coupler, originalCoupledTo));

                    coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Screw_Used);
                    break;
                default:
                    Multiplayer.LogDebug(() => $"Common_ReceiveCouplerInteraction() Unable to return to last state! {originalState}");
                    break;
            }
            return;
        }
        if (flags == CouplerInteractionType.Start && coupler != couplerInteraction)
        {
            Multiplayer.LogDebug(() => $"Common_ReceiveCouplerInteraction() Interaction started [{CurrentID}, {NetId}] isFront: {coupler.isFrontCoupler}");
            //We've received a start signal for a coupler we aren't interacting with
            //Another player must be interacting, so let's block us from tampering with it
            if (coupler?.ChainScript?.knobGizmo)
                coupler.ChainScript.knobGizmo.InteractionAllowed = false;
            if (buttonBase)
                buttonBase.InteractionAllowed = false;

            return;
        }

        if (coupler.ChainScript.state == ChainCouplerInteraction.State.Being_Dragged)
        {
            Multiplayer.LogDebug(() => $"Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}, otherCouplerNetId: {packet.OtherNetId} Being Dragged!");
            coupler.ChainScript?.knobGizmo?.ForceEndInteraction();
        }

        if (flags.HasFlag(CouplerInteractionType.CouplerCouple) && packet.OtherNetId != 0)
        {
            Multiplayer.LogDebug(() => $"1 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags} ");
            if (otherCar != null && (!coupler.IsCoupled() || coupler.coupledTo != otherCoupler))
            {
                Multiplayer.LogDebug(() => $"2 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}");
                StartCoroutine(LooseAttachCoupler(coupler, otherCoupler));
            }
        }

        if (flags.HasFlag(CouplerInteractionType.CouplerPark))
        {
            Multiplayer.LogDebug(() => $"3 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}, current state: {coupler.state}, Chain state:{coupler.ChainScript.state}, isCoupled: {coupler.IsCoupled()}");

            if (coupler.ChainScript.state != ChainCouplerInteraction.State.Attached_Tight)
                StartCoroutine(ParkCoupler(coupler));
            else
                Multiplayer.LogWarning($"Received Park interaction for [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, but coupler is in the wrong state: {coupler.state}, Chain state:{coupler.ChainScript.state}, isCoupled: {coupler.IsCoupled()}");

            Multiplayer.LogDebug(() => $"4 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags} restorestate: {coupler.state}, current state: {coupler.state}, Chain state:{coupler.ChainScript.state}, isCoupled: {coupler.IsCoupled()}");
        }

        if (flags.HasFlag(CouplerInteractionType.CouplerDrop))
        {
            Multiplayer.LogDebug(() => $"5 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags} restorestate: {coupler.state}, current state: {coupler.state}, Chain state:{coupler.ChainScript.state}, isCoupled: {coupler.IsCoupled()}");

            if (coupler.ChainScript.state != ChainCouplerInteraction.State.Attached_Tight)
                StartCoroutine(DangleCoupler(coupler));
            else
                Multiplayer.LogWarning($"Received Dangle interaction for [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, but coupler is in the wrong state: {coupler.state}, Chain state:{coupler.ChainScript.state}, isCoupled: {coupler.IsCoupled()}");
        }

        if (flags.HasFlag(CouplerInteractionType.CouplerLoosen))
        {
            Multiplayer.LogDebug(() => $"6 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], flags: {flags} current state: {coupler.ChainScript.state}");
            if (coupler.ChainScript.state == ChainCouplerInteraction.State.Attached_Tight)
            {
                Multiplayer.LogDebug(() => $"7 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}");
                coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Screw_Used);
            }
            else if (coupler.ChainScript.CurrentState == ChainCouplerInteraction.State.Disabled && coupler.state == ChainCouplerInteraction.State.Attached_Tight)
            {
                //if it's disabled we'll use the internal routines and the state will restore when this player sees the coupling next
                coupler.SetChainTight(false);
            }
        }

        if (flags.HasFlag(CouplerInteractionType.CouplerTighten))
        {
            Multiplayer.LogDebug(() => $"8 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], flags: {flags} current state: {coupler.ChainScript.state}");
            if (coupler.ChainScript.state != ChainCouplerInteraction.State.Attached_Loose || !coupler.IsCoupled())
            {
                Multiplayer.LogWarning($"Incorrect coupling state for tightening! [{TrainCar?.ID}, {NetId}], flags: {flags} current state: {coupler.ChainScript.state} isCoupled: {coupler.IsCoupled()}");
            }
            if (coupler.ChainScript.state == ChainCouplerInteraction.State.Attached_Loose)
            {
                Multiplayer.LogDebug(() => $"9 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}");
                coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Screw_Used);
            }
            else if (coupler.ChainScript.CurrentState == ChainCouplerInteraction.State.Disabled && coupler.state == ChainCouplerInteraction.State.Attached_Loose)
            {
                //if it's disabled we'll use the internal routines and the state will restore when this player sees the coupling next
                coupler.SetChainTight(true);
            }
        }

        if (flags.HasFlag(CouplerInteractionType.CoupleViaUI))
        {
            //if hose connect also requested, then we want everything to connect, otherwise only connect the chain
            bool chainInteraction = !flags.HasFlag(CouplerInteractionType.HoseConnect);

            Multiplayer.LogDebug(() => $"10 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: [{flags}], other coupler: {otherCoupler != null}, chainInteraction: {chainInteraction}");
            if (otherCoupler != null)
            {
                Multiplayer.LogDebug(() => $"10A Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler state: {coupler.state}, other coupler state: {otherCoupler.state}, coupler coupledTo: {coupler?.coupledTo?.train?.ID}, other coupledTo: {otherCoupler?.coupledTo?.train?.ID}, chainInteraction: {chainInteraction}");
                if (coupler.IsCoupled() && coupler.coupledTo != otherCoupler)
                    CouplerLogic.Uncouple(coupler, viaChainInteraction: chainInteraction);
                var car = coupler.IsCoupled() && coupler.coupledTo == otherCoupler
                    ? otherCoupler.train
                    : coupler.CoupleTo(otherCoupler, viaChainInteraction: chainInteraction);

                /* fix for bug in vanilla game */
                coupler.SetChainTight(true);
                if (coupler.ChainScript.enabled)
                {
                    coupler.ChainScript.enabled = false;
                    coupler.ChainScript.enabled = true;
                }
                /* end fix for bug */

                Multiplayer.LogDebug(() => $"10B Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], result: {car != null}");
                //todo: rework hose and MU interactions
            }
        }

        if (flags.HasFlag(CouplerInteractionType.UncoupleViaUI))
        {
            //if hose connect also requested, then we want everything to disconnect, otherwise only disconnect the chain
            bool chainInteraction = !flags.HasFlag(CouplerInteractionType.HoseDisconnect);

            Multiplayer.LogDebug(() => $"11 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}, chainInteraction: {chainInteraction}");
            if (coupler.IsCoupled())
                CouplerLogic.Uncouple(coupler, viaChainInteraction: chainInteraction);

            /* fix for bug in vanilla game */
            coupler.state = ChainCouplerInteraction.State.Parked;
            if (coupler.ChainScript.enabled)
            {
                coupler.ChainScript.enabled = false;
                coupler.ChainScript.enabled = true;
            }
            /* end fix for bug */

            //todo: rework hose and MU interactions 
        }

        if (flags.HasFlag(CouplerInteractionType.CoupleViaRemote))
        {
            Multiplayer.LogDebug(() => $"12 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}, other coupler: {otherCoupler != null}");

            if (TryGetComponent<ExternalCouplingHandler>(out var couplingHandler))
                couplingHandler.Couple();
        }

        if (flags.HasFlag(CouplerInteractionType.UncoupleViaRemote))
        {
            Multiplayer.LogDebug(() => $"13 Common_ReceiveCouplerInteraction() [{TrainCar?.ID}, {NetId}], coupler is front: {packet.IsFrontCoupler}, flags: {flags}");
            if (coupler != null)
            {
                coupler.Uncouple(true, false, false, false);
                MultipleUnitModule.DisconnectCablesIfMultipleUnitSupported(coupler.train, coupler.isFrontCoupler, !coupler.isFrontCoupler);
            }
        }

        //presumably the interaction is now complete, release control to player
        if (coupler?.ChainScript?.knobGizmo)
            coupler.ChainScript.knobGizmo.InteractionAllowed = true;
        if (buttonBase)
            buttonBase.InteractionAllowed = true;
    }

    private IEnumerator LooseAttachCoupler(Coupler coupler, Coupler otherCoupler)
    {
        if (coupler == null || coupler.ChainScript == null ||
            otherCoupler == null || otherCoupler.ChainScript == null ||
            otherCoupler.ChainScript.ownAttachPoint == null)
        {
            Multiplayer.LogDebug(() => $"LooseAttachCoupler() [{TrainCar?.ID}], Null reference! Coupler: {coupler != null}, chainscript: {coupler?.ChainScript != null}, other coupler: {otherCoupler != null}, other chainscript: {otherCoupler?.ChainScript != null}, other attach point: {otherCoupler?.ChainScript?.ownAttachPoint}");
            yield break;
        }

        ChainCouplerInteraction ccInteraction = coupler.ChainScript;

        if (ccInteraction.CurrentState == ChainCouplerInteraction.State.Disabled)
        {
            //since it's disabled FSM events won't fire. Force a coupling if required, otherwise set state ready for player visibility trigger

            if (coupler.coupledTo == null)
                coupler.CoupleTo(otherCoupler, true, true);
            else
                coupler.state = ChainCouplerInteraction.State.Attached_Loose;

            yield break;
        }

        //Simulate player pickup
        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Picked_Up_By_Player);

        //Set the knob position to the other coupler's hook
        Vector3 targetHookPos = otherCoupler.ChainScript.ownAttachPoint.transform.position;
        coupler.ChainScript.knob.transform.position = targetHookPos;

        //allow the follower and IK solver to update
        coupler.ChainScript.Update_Being_Dragged();

        //we need to allow the IK solver to calculate the chain ring anchor's position over a number of iterations
        int x = 0;
        float distance = float.MaxValue;
        //game checks for Vector3.Distance(this.chainRingAnchor.position, this.closestAttachPoint.transform.position) < attachDistanceThreshold;
        while (distance >= ChainCouplerInteraction.attachDistanceThreshold && x < MAX_COUPLER_ITERATIONS)
        {
            distance = Vector3.Distance(ccInteraction.chainRingAnchor.position, targetHookPos);

            x++;
            yield return new WaitForSeconds(ccInteraction.ROTATION_SMOOTH_DURATION);
        }

        Multiplayer.LogDebug(() => $"LooseAttachCoupler() [{TrainCar?.ID}], Coupler: {coupler?.train?.ID}, OtherCoupler: {otherCoupler?.train?.ID}, Distance: {distance}, Iterations: {x}");

        //Drop the chain
        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Dropped_By_Player);

        Multiplayer.LogDebug(() => $"LooseAttachCoupler() [{TrainCar?.ID}], Coupler: {coupler?.train?.ID}, OtherCoupler: {otherCoupler?.train?.ID}, CoupledTo: {coupler.coupledTo?.train?.ID}");
    }

    private IEnumerator ParkCoupler(Coupler coupler)
    {
        ChainCouplerInteraction ccInteraction = coupler.ChainScript;

        if (ccInteraction.CurrentState == ChainCouplerInteraction.State.Disabled)
        {
            //since it's disabled FSM events won't fire, but state will be restored when the coupling is visible to the current player
            if (coupler.state == ChainCouplerInteraction.State.Attached_Loose && coupler.coupledTo != null)
                coupler.Uncouple(true, false, false, true);

            coupler.state = ChainCouplerInteraction.State.Parked;

            yield break;
        }

        //Simulate player pickup
        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Picked_Up_By_Player);

        //Set the knob position
        Vector3 parkPos = coupler.ChainScript.parkedAnchor.position;

        coupler.ChainScript.knob.transform.position = parkPos;

        //allow the follower and IK solver to update
        coupler.ChainScript.Update_Being_Dragged();

        //we need to allow the IK solver to calculate the chain ring anchor's position over a number of iterations
        int x = 0;
        float distance = float.MaxValue;
        //game checks for Vector3.Distance(this.chainRingAnchor.position, this.parkedAnchor.position) < parkDistanceThreshold;
        //need to make sure we are closer than the threshold before dropping
        while (distance > ChainCouplerInteraction.parkDistanceThreshold && x < MAX_COUPLER_ITERATIONS)
        {
            distance = Vector3.Distance(ccInteraction.chainRingAnchor.position, ccInteraction.parkedAnchor.position);

            x++;
            yield return new WaitForSeconds(ccInteraction.ROTATION_SMOOTH_DURATION);
        }

        //Drop the chain
        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Dropped_By_Player);
    }

    private IEnumerator DangleCoupler(Coupler coupler)
    {
        ChainCouplerInteraction ccInteraction = coupler.ChainScript;

        if (ccInteraction.CurrentState == ChainCouplerInteraction.State.Disabled)
        {
            //since it's disabled FSM events won't fire, but state will be restored when the coupling is visible to the current player
            if (coupler.state == ChainCouplerInteraction.State.Attached_Loose && coupler.coupledTo != null)
                coupler.Uncouple(true, false, false, true);

            coupler.state = ChainCouplerInteraction.State.Dangling;

            yield break;
        }

        //Simulate player pickup
        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Picked_Up_By_Player);

        Vector3 parkPos = coupler.ChainScript.parkedAnchor.position;

        //Set the knob position
        coupler.ChainScript.knob.transform.position = parkPos + Vector3.down; //ensure we are not near the park anchor or other car's anchor

        //allow the follower and IK solver to update
        coupler.ChainScript.Update_Being_Dragged();

        //we need to allow the IK solver to calculate the chain ring anchor's position over a number of iterations
        int x = 0;
        float distance = float.MinValue;
        //game checks for Vector3.Distance(this.chainRingAnchor.position, this.parkedAnchor.position) < parkDistanceThreshold;
        //to determine if it should be parked or dangled, need to make sure we are at least at the threshold before dropping
        while (distance <= ChainCouplerInteraction.parkDistanceThreshold && x < MAX_COUPLER_ITERATIONS)
        {
            distance = Vector3.Distance(ccInteraction.chainRingAnchor.position, ccInteraction.parkedAnchor.position);

            x++;
            yield return new WaitForSeconds(ccInteraction.ROTATION_SMOOTH_DURATION);
        }

        //Drop the chain
        coupler.ChainScript.fsm.Fire(ChainCouplerInteraction.Trigger.Dropped_By_Player);
    }

    public void Common_ReceivePaintThemeUpdate(TrainCarPaint.Target target, PaintTheme paint)
    {
        TrainCarPaint targetPaint = null;

        if (target == TrainCarPaint.Target.Interior)
        {
            Multiplayer.LogDebug(() => $"Received Paint Theme update for [{CurrentID}, {NetId}], targeting Interior");
            targetPaint = TrainCar.PaintInterior;
        }
        else if (target == TrainCarPaint.Target.Exterior)
        {
            Multiplayer.LogDebug(() => $"Received Paint Theme update for [{CurrentID}, {NetId}], targeting Exterior");
            targetPaint = TrainCar.PaintExterior;
        }

        if (targetPaint == null /*|| !targetPaint.IsSupported(paint)*/  )
        {
            Multiplayer.LogWarning($"Received Paint Theme update for [{CurrentID}, {NetId}], but {paint?.AssetName} is not supported");
            return;
        }

        targetPaint.currentTheme = paint;
        targetPaint.UpdateTheme();
        TrainCar.OnPaintThemeChanged(targetPaint);
    }
    #endregion

    #region Client

    #endregion
}
