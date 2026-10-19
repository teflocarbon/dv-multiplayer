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
using Multiplayer.Components.Networking.Player;
using Multiplayer.Core.Trains;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Common.Train;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Components.Networking.Train;

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

    private bool IsAuthoritativeTopologyObservationSuppressed =>
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
    // How far an unleased control's port must be from the host's last value to count as the
    // player's change rather than simulation feedback (Client_UnleasedChange).
    private const float UNLEASED_CHANGE_TOLERANCE = 0.02f;
    // And how long after a host update to a port its control may still be catching up to it.
    private const float UNLEASED_CHANGE_SETTLE_SECONDS = 1.5f;
    private const uint MIN_KINEMATIC_CYCLES = 10;
    private const float DISTANCE_TOLERANCE = 2f;
    private const float MAX_PAINT_DISTANCE_SQ = (CommsRadioPaintjob.SIGNAL_RANGE + DISTANCE_TOLERANCE) * (CommsRadioPaintjob.SIGNAL_RANGE + DISTANCE_TOLERANCE);
    // Periodic full-sync positions are only a safety net: TrainSpeedQueue steers on-rail cars to
    // the host's bogie positions continuously, and snapping a moving car to a latency-old pose
    // yanks it backwards. Only snap when the car is clearly somewhere else.
    private const float POSITION_UPDATE_THRESHOLD = 5f;
    private const float INTERIOR_CONTROLS_TIMEOUT = 2f;

    #region Port and Fuse Map

    private static readonly Dictionary<uint, string> netIdToPort = [];
    private static readonly Dictionary<string, uint> portToNetId = [];
    private static readonly Dictionary<uint, string> netIdToFuse = [];
    private static readonly Dictionary<string, uint> fuseToNetId = [];

    static uint GetPortNetId(string portId)
    {
        if (portToNetId.TryGetValue(portId, out var netId))
            return netId;

        netId = StringHashing.Fnv1aHash(portId);

        Multiplayer.LogDebug(() => $"GetPortNetId({portId}) Registering with {netId}");

        netIdToPort[netId] = portId;
        portToNetId[portId] = netId;

        return netId;
    }

    static string GetPort(uint netId)
    {
        netIdToPort.TryGetValue(netId, out var portId);
        return portId;
    }

    static uint GetFuseNetId(string fuseId)
    {
        if (fuseToNetId.TryGetValue(fuseId, out var netId))
            return netId;

        netId = StringHashing.Fnv1aHash(fuseId);
        netIdToFuse[netId] = fuseId;
        fuseToNetId[fuseId] = netId;

        return netId;
    }
    static string GetFuse(uint netId)
    {
        netIdToFuse.TryGetValue(netId, out var portId);
        return portId;
    }
    #endregion

    public float CarLengthSq => CarSpawner.Instance.carLiveryToCarLength[TrainCar.carLivery] * CarSpawner.Instance.carLiveryToCarLength[TrainCar.carLivery];

    public string CurrentID { get; private set; }
    public TrainCar TrainCar;
    public uint TicksSinceSync = uint.MaxValue;

    public uint lastTickProcessed = 0;
    public uint ControlRevision { get; private set; }
    public bool HasPlayers => PlayerManager.Car == TrainCar || GetComponentInChildren<NetworkedPlayer>() != null;

    private Bogie bogie1;
    private Bogie bogie2;
    private BrakeSystem brakeSystem;

    private bool hasSimFlow;
    private SimulationFlow simulationFlow;
    SimController simController;
    public FireboxSimController firebox;
    // The compressor's activation signal: typed CONTROL, but the brake system sets it.
    private readonly HashSet<uint> compressorSignalPorts = new();
    private Port compressorSignalPort;
    // Ports vanilla writes every frame on every game from that game's own car: TractionPortsFeeder's
    // forward speed, wheel rpm and wheel speed, and a steam boiler's tilt (BoilerSimController).
    // Each game feeds its own, so they aren't synced. Applying the host's too gave them two
    // writers, and a speedometer flicked between the two values.
    private readonly HashSet<uint> locallyFedPorts = new();
    // Which CONTROL ports players drive: the only ones a client sends, or the host takes from one.
    private ControlPortWriters<uint> portWriters;
    public CoalPileSimController coalPile;
    private readonly Dictionary<TrainDamage, TrainDamage.HealthChanged> trainDamageDelegates = [];

    private HashSet<uint> dirtyPorts;
    private Dictionary<uint, float> lastSentPortValues;
    private HashSet<uint> dirtyFuses;
    private readonly HashSet<TrainCarPaint> dirtyPaints = [];
    private readonly Dictionary<string, float> lastSentTrainDamages = [];

    private InteriorControlsManager interiorControlsManager;
    private readonly Dictionary<ControlImplBase, Action<ValueChangedEventArgs>> scrollableControlDelegates = [];
    private Dictionary<InteriorControlsManager.ControlType, OverridableBaseControl> controlTypeToControl = [];
    private readonly Dictionary<uint, OverridableBaseControl> portToBaseControl = [];
    private readonly Dictionary<IScrollable, Coroutine> scrollingControls = [];
    private readonly Dictionary<ControlImplBase, Action> usedControlDelegates = [];
    // A control's first port -> the other ports it feeds.
    private readonly Dictionary<uint, uint[]> linkedPortNetIds = [];
    // Controls pressed without a grab (VR buttons and switches) whose lease is handed back shortly.
    private readonly Dictionary<ControlImplBase, Coroutine> usedControlReleases = [];
    private const float USED_CONTROL_LEASE_SECONDS = 0.5f;

    private bool handbrakeDirty;

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

    // Client: controls this client changed in operations the host hasn't committed back yet.
    // Accepted commits are broadcast to everyone, including the sender, a round trip later; a
    // commit for an older operation must not drag a lever back from where the player has since
    // moved it (or leave it there once they stop).
    private readonly Dictionary<uint, ulong> client_pendingPortOperations = new();
    private readonly Dictionary<uint, ulong> client_pendingFuseOperations = new();
    private ulong client_pendingHandbrakeOperation;
    private bool mainResPressureDirty;
    private bool brakeOverheatDirty;
    private float lastSentMainReservoirPressure = float.NaN;
    private float lastSentBrakePipePressure = float.NaN;
    private float lastSentBrakeCylinderPressure = float.NaN;
    private uint lastBrakeStateTick;

    public bool BogieTracksDirty;
    private bool cargoStateDirty;
    private bool cargoHealthDirty;
    private bool cargoIsLoading;
    public byte CargoModelIndex = byte.MaxValue;
    private bool carHealthDirty;
    private bool sendCouplers;
    private bool sendCables;
    private bool topologyDirty;

    public bool IsDestroying;

    #region Server Variables
    //Coupler interaction
    private bool frontInteracting = false;
    private bool rearInteracting = false;

    private ServerPlayer frontInteractionPlayer;
    private ServerPlayer rearInteractionPlayer;

    private readonly Dictionary<uint, ServerPlayer> portAuthority = [];

    #endregion

    #region Client Variables

    public bool Client_Initialized { get; private set; }
    public TickedQueue<float> Client_trainSpeedQueue;
    public TickedQueue<RigidbodySnapshot> Client_trainRigidbodyQueue;
    public NetworkedBogie client_bogie1Queue;
    public NetworkedBogie client_bogie2Queue;


    private Coupler couplerInteraction;

    // Chains the local player is dragging right now. Vanilla uncouples locally when a coupled chain
    // is picked up; the host only hears about it on the drop. A topology snapshot applied mid-drag
    // re-coupled the cars under the chain, and the drop then went unreported. Snapshots wait while a
    // drag is in progress (checked live, so a destroyed chain can never block them).
    private static readonly HashSet<Coupler> localChainDrags = new();

    internal static bool IsLocalChainDragInProgress
    {
        get
        {
            localChainDrags.RemoveWhere(coupler => coupler == null || coupler.ChainScript == null ||
                coupler.ChainScript.CurrentState != ChainCouplerInteraction.State.Being_Dragged);
            return localChainDrags.Count > 0;
        }
    }
    private ChainCouplerInteraction.State originalState;
    private Coupler originalCoupledTo;

    private uint kinematicCycles = 0;

    // Host: where a derailed car was when it last moved more than DerailedSettleDistance.
    private const float DerailedSettleDistance = 0.02f;
    private Vector3 derailedAnchor;
    private uint derailedAnchorTick;
    private bool hasDerailedAnchor;

    /// <summary>Host: whether a derailed car has moved less than 2 cm in the last second.</summary>
    public bool Server_DerailedSettled(Vector3 position, uint tick)
    {
        if (!hasDerailedAnchor || (position - derailedAnchor).sqrMagnitude > DerailedSettleDistance * DerailedSettleDistance)
        {
            derailedAnchor = position;
            derailedAnchorTick = tick;
            hasDerailedAnchor = true;
            return false;
        }
        return tick - derailedAnchorTick >= NetworkLifecycle.TICK_RATE;
    }

    private readonly Dictionary<uint, bool> portNetIdToBlockState = [];
    private readonly Dictionary<uint, ControlImplBase> portNetIdToControl = [];
    private readonly Dictionary<ControlImplBase, uint> controlToPortNetId = [];
    // The server deliberately does not echo a successful lease grant to its requester.
    // Track the leases we requested locally so client simulation feedback cannot be
    // bundled with a control the client does not own.
    private readonly HashSet<uint> locallyOwnedPortAuthorities = [];
    // Each control port's value as the host last had it, to tell a player's change to a control
    // that took no lease from the host's own values echoing back (Client_UnleasedChange).
    private readonly Dictionary<uint, float> client_hostPortValues = [];
    private readonly Dictionary<uint, float> client_hostPortReceivedAt = [];
    #endregion

    #region Common Variables

    #endregion

    protected override bool IsIdServerAuthoritative => true;

    protected override void Awake()
    {
        base.Awake();

        TrainCar = GetComponent<TrainCar>();
        trainCarsToNetworkedTrainCars[TrainCar] = this;
        EntityDebugRegistry.RegisterTrain(this);
        DebugRuntime.Publish("train", "train.registered", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "TrainCar", entityId: NetId.ToString(), data: DebugValueSnapshotter.SnapshotObject(new { CurrentID, NetId }));

        TrainCar.LogicCarInitialized += OnLogicCarInitialised;

        bogie1 = TrainCar.Bogies[0];
        bogie2 = TrainCar.Bogies[1];

        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkTrainsetWatcher.Instance.CheckInstance(); // Ensure the NetworkTrainsetWatcher is initialized
            Client_Initialized = true;
        }
        else
        {
            Client_trainSpeedQueue = TrainCar.GetOrAddComponent<TrainSpeedQueue>();
            Client_trainRigidbodyQueue = TrainCar.GetOrAddComponent<NetworkedRigidbody>();
            StartCoroutine(Client_InitLater());
        }
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

                //Locos with tenders and tenders only have one chainscript each, no trainscript is used for the hitch between the loco and tender
                if (coupler.ChainScript != null)
                    coupler.ChainScript.StateChanged += (state) => { Client_CouplerStateChange(state, coupler); };
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

            TrainCar.InteriorLoaded += OnTrainCarInteriorLoaded;
            TrainCar.InteriorAboutToBeUnloaded += OnTrainCarInteriorUnloaded;

            if (TrainCar.loadedInterior != null)
                OnTrainCarInteriorLoaded(TrainCar.loadedInterior.gameObject);

            if (simulationFlow != null)
            {
                if (simController.tractionPortsFeeder is { } feeder)
                    foreach (string portId in new[] { feeder.forwardSpeedPortId, feeder.wheelRpmPortId, feeder.wheelSpeedKmhPortId })
                        if (!string.IsNullOrEmpty(portId))
                            locallyFedPorts.Add(GetPortNetId(portId));
                foreach (BoilerSimController boiler in TrainCar.GetComponentsInChildren<BoilerSimController>(true))
                    if (!string.IsNullOrEmpty(boiler.anglePortId))
                        locallyFedPorts.Add(GetPortNetId(boiler.anglePortId));

                dirtyPorts = new HashSet<uint>(simulationFlow.fullPortIdToPort.Count);
                lastSentPortValues = new Dictionary<uint, float>(dirtyPorts.Count);
                foreach (KeyValuePair<string, Port> kvp in simulationFlow.fullPortIdToPort)
                {
                    var portNetId = GetPortNetId(kvp.Key);
                    if (locallyFedPorts.Contains(portNetId))
                        continue;
                    if (kvp.Value.valueType == PortValueType.CONTROL || NetworkLifecycle.Instance.IsHost())
                    {
                        if (!NetworkLifecycle.Instance.IsHost())
                            client_hostPortValues[portNetId] = kvp.Value.Value;
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

            // Ports pulsed on an event (adding coal, igniting firebox, etc)
            // A player adding coal: FireboxTransferCoalPatch; a player shovelling from the coal pile:
            // CoalPileTransferCoalPatch (the coal ports are a stoker's too).
            if (firebox != null)
            {
                firebox.fireboxIgnitionPort.ValueUpdatedInternally += Client_OnIgnite;      //Player igniting firebox
            }
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

        NetworkLifecycle.Instance.OnTick += Common_OnTick;

        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.OnTick += Server_OnTick;
            NetworkLifecycle.Instance.Server.PlayerDisconnected += Server_OnPlayerDisconnect;

            bogie1.TrackChanged += Server_BogieTrackChanged;
            bogie2.TrackChanged += Server_BogieTrackChanged;

            TrainCar.frontCoupler.Uncoupled += Server_CouplerUncoupled;
            TrainCar.rearCoupler.Uncoupled += Server_CouplerUncoupled;
            TrainCar.frontCoupler.Coupled += Server_CouplerCoupled;
            TrainCar.rearCoupler.Coupled += Server_CouplerCoupled;

            TrainCar.CarDamage.CarEffectiveHealthStateUpdate += Server_CarHealthUpdate;

            //find all TrainDamages and subscribe
            if (TryGetComponent<DamageController>(out DamageController damageController) && damageController != null)
            {
                var trainDamageFields = typeof(DamageController)
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(field => field.FieldType == typeof(TrainDamage))
                    .Select(field => new { Field = field, Damage = (TrainDamage)field.GetValue(damageController) })
                    .Where(value => value.Damage != null)
                    .ToArray();

                if (trainDamageFields != null && trainDamageFields.Length > 0)
                {
                    for (int i = 0; i < trainDamageFields.Length; i++)
                    {
                        var fieldName = trainDamageFields[i].Field.Name;
                        var fieldValue = trainDamageFields[i].Damage;

                        //create a delegate for each field
                        void DamagesUpdate(float health) => Server_TrainDamagesHealthUpdate(fieldName, health);

                        //subscribe to the event
                        trainDamageFields[i].Damage.HealthPercentageChanged += DamagesUpdate;

                        //store delegates and set a last sent value to an impossible value
                        trainDamageDelegates.Add(fieldValue, DamagesUpdate);
                        lastSentTrainDamages.Add(fieldName, -1f);
                    }
                }
            }

            if (brakeSystem != null)
            {
                brakeSystem.MainResPressureChanged += Server_MainResUpdate;
                brakeSystem.heatController.OverheatingActiveStateChanged += Server_BrakeHeatUpdate;
            }

            StartCoroutine(Server_WaitForLogicCar());
        }

        NetworkLifecycle.Instance?.Client.SendTrainSyncRequest(NetId);
    }

    private void OnTrainCarInteriorLoaded(GameObject interior)
    {
        Multiplayer.LogDebug(() => $"OnTrainCarInteriorLoaded() {CurrentID}, interior is null: {interior == null}");

        StartCoroutine(WaitForInterior());
    }

    private IEnumerator WaitForInterior()
    {
        float time = Time.time;
        interiorControlsManager = null;

        yield return new WaitUntil
        (
            () =>
            {
                return TrainCar?.loadedInterior != null || Time.time - time > INTERIOR_CONTROLS_TIMEOUT;
            }
        );

        yield return new WaitForFixedUpdate();

        if (TrainCar?.loadedInterior == null)
        {
            Multiplayer.LogError($"TrainCar {CurrentID} failed to load an interior");
            yield break;
        }

        time = Time.time;

        yield return new WaitUntil
        (
            () =>
            {
                return TrainCar.loadedInterior.TryGetComponent<InteriorControlsManager>(out interiorControlsManager) || Time.time - time > INTERIOR_CONTROLS_TIMEOUT;
            }
        );

        CoroutineManager.Instance.StartCoroutine(HookControls(interiorControlsManager));
    }

    private IEnumerator HookControls(InteriorControlsManager interiorControlsManager)
    {
        yield return new WaitUntil(() => interiorControlsManager.Initialized);

        Multiplayer.LogDebug(() => $"HookControls() Hooking controls for car {CurrentID}, found {interiorControlsManager?.controls?.Count} controls");

        // Controls finish setting up over the first frames. Wait for them all at once, up to the
        // timeout: waiting for each in turn let every control with no port (handbrake, fuses) use up
        // the whole timeout, holding up the ones after it by seconds.
        float timeOut = Time.time;
        yield return new WaitUntil
        (
            () =>
                interiorControlsManager.controls.Values.All(ControlReady) ||
                Time.time - timeOut > INTERIOR_CONTROLS_TIMEOUT
        );

        // Find all control overrides
        foreach (var kvp in interiorControlsManager.controls)
        {
            var control = kvp.Value;
            var key = kvp.Key;

            var controlPortId = ControlPortId(control);

            if (string.IsNullOrEmpty(controlPortId))
            {
                Multiplayer.LogWarning($"Unable to hook control {control.overridableBaseControl?.name ?? control.controlImplBase?.spec?.name} ({key}), has no controlPortId on car [{CurrentID}, {NetId}]");
                continue;
            }

            Multiplayer.LogDebug(() => $"HookControls() Control [{controlPortId}] found on car {CurrentID}");
            var netId = GetPortNetId(controlPortId);

            if (control.controlImplBase == null)
            {
                Multiplayer.LogWarning($"Unable to hook control [{controlPortId}, {netId}], no control implementation on car {CurrentID}");
                continue;
            }

            HookControl(control.controlImplBase, netId);

            // Handle scroll events for scrollable controls
            if (control.scrollable != null)
            {
                void ScrollChanged(ValueChangedEventArgs args) => Client_ControlScrolled(control);
                scrollableControlDelegates[control.controlImplBase] = ScrollChanged;
                control.controlImplBase.ValueChanged += ScrollChanged;
            }
        }

        // Controls that drive a sim port but aren't in the manager's list, like the DE2's cab fan.
        foreach (InteractablePortFeeder feeder in interiorControlsManager.GetComponentsInChildren<InteractablePortFeeder>(true))
        {
            ControlImplBase implementation = feeder.GetComponent<ControlImplBase>();
            if (implementation == null || string.IsNullOrEmpty(feeder.portId) || controlToPortNetId.ContainsKey(implementation))
                continue;

            uint netId = GetPortNetId(feeder.portId);
            if (portNetIdToControl.ContainsKey(netId))
                continue;

            Multiplayer.LogDebug(() => $"HookControls() Unlisted control [{feeder.portId}] found on car {CurrentID}");
            HookControl(implementation, netId);
        }

        // The initial car-state refresh can arrive before a streamed cabin and its
        // physical controls exist. Request the authoritative state again now so
        // default-engaged handbrakes and all SimFlow-backed levers are projected onto
        // the newly loaded interior instead of retaining their prefab defaults.
        Client_RequestInteriorResync();
    }

    private const float INTERIOR_RESYNC_INTERVAL = 10f;
    private float client_nextInteriorResyncAt;
    private bool client_interiorResyncPending;

    // A player standing at the distance an interior loads at reloads it every few seconds, and
    // each full resync makes the host send the whole car to everyone (playtest 2: one car every
    // 5-15 s, two requests a time). One per car per interval; any more in it make one at its end.
    private void Client_RequestInteriorResync()
    {
        if (Time.realtimeSinceStartup >= client_nextInteriorResyncAt)
        {
            client_nextInteriorResyncAt = Time.realtimeSinceStartup + INTERIOR_RESYNC_INTERVAL;
            NetworkLifecycle.Instance?.Client.SendTrainSyncRequest(NetId);
        }
        else if (!client_interiorResyncPending)
        {
            client_interiorResyncPending = true;
            CoroutineManager.Instance.StartCoroutine(Client_InteriorResyncLater());
        }
    }

    private IEnumerator Client_InteriorResyncLater()
    {
        yield return new WaitForSecondsRealtime(client_nextInteriorResyncAt - Time.realtimeSinceStartup);
        client_interiorResyncPending = false;
        if (this != null)
            Client_RequestInteriorResync();
    }

    // Most controls name their port through the controls overrider. Some only through the feeder
    // that writes them into the simulation: the DE2's wipers, cab light and dash light.
    private static string ControlPortId(InteriorControlsManager.ControlReference control)
    {
        string portId = control.overridableBaseControl?.portId;
        if (!string.IsNullOrEmpty(portId))
            return portId;
        return control.controlImplBase != null ? control.controlImplBase.GetComponent<InteractablePortFeeder>()?.portId : null;
    }

    // Set up, or never going to have a port: no overrider and no feeder (a handbrake, a fuse).
    private static bool ControlReady(InteriorControlsManager.ControlReference control)
    {
        if (control.controlImplBase == null)
            return false;
        return !string.IsNullOrEmpty(ControlPortId(control)) ||
            (control.overridableBaseControl == null && control.controlImplBase.GetComponent<InteractablePortFeeder>() == null);
    }

    private void HookControl(ControlImplBase implementation, uint netId)
    {
        // Normally in the prefab scan already; a control only the loaded cab has counts too.
        portWriters?.AddPlayerPort(netId);
        foreach (InteractablePortFeeder feeder in implementation.GetComponents<InteractablePortFeeder>())
            if (!string.IsNullOrEmpty(feeder.portId))
                portWriters?.AddPlayerPort(GetPortNetId(feeder.portId));

        // Check for ObiRopeGrabHandler used by steam whistles
        ObiRopeGrabAreaHandler ObiGrabHandler = implementation.transform?.parent?.GetComponentInChildren<ObiRopeGrabAreaHandler>(false);
        if (ObiGrabHandler != null)
        {
            // Handle grab/ungrab events
            ObiGrabHandler.Grabbed += Client_ControlGrabbed;
            ObiGrabHandler.Ungrabbed += Client_ControlUngrabbed;

            portNetIdToControl[netId] = ObiGrabHandler;
            controlToPortNetId[ObiGrabHandler] = netId;
        }
        else
        {
            // Standard control grab/ungrab events
            implementation.Grabbed += Client_ControlGrabbed;
            implementation.Ungrabbed += Client_ControlUngrabbed;

            // Buttons and switches in VR, and keyboard presses, work through Use() and never
            // raise Grabbed.
            void Used() => Client_ControlUsed(implementation);
            usedControlDelegates[implementation] = Used;
            implementation.Used += Used;

            portNetIdToControl[netId] = implementation;
            controlToPortNetId[implementation] = netId;

            uint[] linked = implementation.GetComponents<InteractablePortFeeder>()
                .Where(f => !string.IsNullOrEmpty(f.portId))
                .Select(f => GetPortNetId(f.portId))
                .Where(id => id != netId && !portNetIdToControl.ContainsKey(id))
                .Distinct().ToArray();
            if (linked.Length > 0)
            {
                linkedPortNetIds[netId] = linked;
                foreach (uint id in linked)
                    portNetIdToControl[id] = implementation;
            }
        }

        if (portNetIdToBlockState.TryGetValue(netId, out var isBlocked) && isBlocked)
        {
            Multiplayer.LogDebug(() => $"HookControl() Control [{implementation.name}, {netId}] is blocked on car {CurrentID}");
            Client_ReceiveAuthorityUpdate(netId, ControlAuthorityState.Blocked);
        }
    }

    private void OnTrainCarInteriorUnloaded(GameObject interior)
    {
        Multiplayer.LogDebug(() => $"OnTrainCarInteriorUnloaded() {CurrentID}");

        // A control used just before the cab unloads still holds its lease: give it back.
        foreach (var kvp in usedControlReleases)
        {
            if (kvp.Value != null)
                StopCoroutine(kvp.Value);
            if (controlToPortNetId.TryGetValue(kvp.Key, out uint usedPortNetId))
                Client_ReleasePortAuthority(usedPortNetId);
        }
        usedControlReleases.Clear();

        foreach (var kvp in usedControlDelegates)
        {
            if (kvp.Key != null)
                kvp.Key.Used -= kvp.Value;
        }
        usedControlDelegates.Clear();

        foreach (var control in controlToPortNetId.Keys)
        {
            if (control == null)
                continue;

            if (control.IsGrabbed())
                Client_ControlUngrabbed(control);

            control.Grabbed -= Client_ControlGrabbed;
            control.Ungrabbed -= Client_ControlUngrabbed;
        }

        foreach (var kvp in scrollingControls)
        {
            if (kvp.Key == null)
                continue;

            StopCoroutine(kvp.Value);
        }
        scrollingControls.Clear();

        foreach (var kvp in scrollableControlDelegates)
        {
            if (kvp.Key != null && kvp.Value != null)
                kvp.Key.ValueChanged -= kvp.Value;
        }
        scrollableControlDelegates.Clear();

        portNetIdToControl.Clear();
        linkedPortNetIds.Clear();
        controlToPortNetId.Clear();

        interiorControlsManager = null;
    }

    /// <summary>
    /// Set when the car is deleted (or returned to DV's car pool). The component destroys itself
    /// then, but Unity only does that at the end of the frame; see <see cref="OnDisable"/>.
    /// </summary>
    internal bool Retired { get; private set; }

    public void OnDisable()
    {
        if (UnloadWatcher.isQuitting || Retired)
            return;

        DebugRuntime.Publish("train", "train.unregistered", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            entityType: "TrainCar", entityId: NetId.ToString(), data: DebugValueSnapshotter.SnapshotObject(new { CurrentID, NetId }));
        EntityDebugRegistry.Unregister("TrainCar", NetId.ToString());

        //Clean dictionaries
        trainCarsToNetworkedTrainCars.Remove(TrainCar);
        trainCarIdToNetworkedTrainCars.Remove(CurrentID);
        trainCarIdToTrainCars.Remove(CurrentID);

        foreach (Coupler coupler in TrainCar.couplers)
            hoseToCoupler.Remove(coupler.hoseAndCock);

        if (NetworkLifecycle.Instance.IsHost() && !UnloadWatcher.isUnloading)
            NetworkLifecycle.Instance.Server?.CommitObservedTrainTopology();

        //stop tracking client events
        NetworkLifecycle.Instance.OnTick -= Common_OnTick;

        if (firebox != null)
        {
            firebox.fireboxIgnitionPort.ValueUpdatedInternally -= Client_OnIgnite;      //Player igniting firebox
        }

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
        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.OnTick -= Server_OnTick;
            NetworkLifecycle.Instance.Server.PlayerDisconnected -= Server_OnPlayerDisconnect;

            bogie1.TrackChanged -= Server_BogieTrackChanged;
            bogie2.TrackChanged -= Server_BogieTrackChanged;

            TrainCar.frontCoupler.Uncoupled -= Server_CouplerUncoupled;
            TrainCar.rearCoupler.Uncoupled -= Server_CouplerUncoupled;
            TrainCar.frontCoupler.Coupled -= Server_CouplerCoupled;
            TrainCar.rearCoupler.Coupled -= Server_CouplerCoupled;

            TrainCar.CarDamage.CarEffectiveHealthStateUpdate -= Server_CarHealthUpdate;

            //Unsubscribe from damage updates
            if (trainDamageDelegates != null && lastSentTrainDamages.Count > 0)
                foreach (var kvp in trainDamageDelegates)
                    kvp.Key.HealthPercentageChanged -= kvp.Value;

            if (brakeSystem != null)
            {
                brakeSystem.MainResPressureChanged -= Server_MainResUpdate;
                brakeSystem.heatController.OverheatingActiveStateChanged -= Server_BrakeHeatUpdate;
            }

            if (TrainCar.logicCar != null)
            {
                TrainCar.logicCar.CargoLoaded -= Server_OnCargoLoaded;
                TrainCar.logicCar.CargoUnloaded -= Server_OnCargoUnloaded;
            }
        }

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
    private IEnumerator Server_WaitForLogicCar()
    {
        while (TrainCar.logicCar == null)
            yield return null;

        TrainCar.logicCar.CargoLoaded += Server_OnCargoLoaded;
        TrainCar.logicCar.CargoUnloaded += Server_OnCargoUnloaded;

        if (TrainCar.CargoDamage)
            TrainCar.CargoDamage.CargoEffectiveHealthStateUpdate += Server_CargoHealthUpdate;

        Server_DirtyAllState();
    }

    public void Server_DirtyAllState()
    {
        handbrakeDirty = true;
        mainResPressureDirty = true;
        cargoStateDirty = true;
        cargoHealthDirty = true;
        cargoIsLoading = true;
        carHealthDirty = true;
        sendCouplers = true;
        sendCables = true;
        Server_ResyncPose();

        if (!hasSimFlow)
            return;
        foreach (string portId in simulationFlow.fullPortIdToPort.Keys)
        {
            var netId = GetPortNetId(portId);
            dirtyPorts.Add(netId);
        }

        foreach (string fuseId in simulationFlow.fullFuseIdToFuse.Keys)
        {
            var netId = GetFuseNetId(fuseId);
            dirtyFuses.Add(netId);
        }
    }

    /// <summary>
    /// Send the car's pose and both bogies' tracks and spans on the next tick, reliably, even if
    /// it's standing still (a stopped set is otherwise only resent every couple of seconds).
    /// </summary>
    public void Server_ResyncPose()
    {
        BogieTracksDirty = true; // makes the set's packet reliable
        TicksSinceSync = uint.MaxValue / 2; // full pose on the next tick (a client rerailing to it needs one); not MaxValue, the tick's ++ would wrap it to 0
    }

    /// <summary>
    /// The host is rerailing this car or moving it to another track: once vanilla has (a physics
    /// step later), send clients where it is now, so their followers aren't left aiming at
    /// where it was.
    /// </summary>
    public void Server_ResyncAfterRelocation() => CoroutineManager.Instance.StartCoroutine(Server_ResyncAfterRelocationCoro());

    private IEnumerator Server_ResyncAfterRelocationCoro()
    {
        yield return new WaitForFixedUpdate();
        for (float t0 = Time.realtimeSinceStartup; TrainCar != null && TrainCar.IsTeleporting && Time.realtimeSinceStartup - t0 < 5f;)
            yield return null;
        if (this != null && TrainCar != null)
            Server_ResyncPose();
    }

    public bool Server_ValidateClientSimFlowPacket(ServerPlayer player, CommonTrainPortsPacket packet)
    {
        // Only allow control ports to be updated by clients
        if (hasSimFlow)
            foreach (uint portNetId in packet.PortIds)
            {

                var portId = GetPort(portNetId);
                if (simulationFlow.TryGetPort(portId, out Port _))
                {
                    // Only controls players drive: the others are the host's simulation's to set.
                    bool holdsLease = portAuthority.TryGetValue(portNetId, out ServerPlayer owner) && owner == player;
                    if (portWriters == null || !portWriters.HostAccepts(portNetId, holdsLease))
                    {
                        NetworkLifecycle.Instance.Server.LogWarning($"Player {player.Username} tried to send a port players don't drive ({portWriters?.Classify(portNetId)})! ([{portId}, {portNetId}] on [{CurrentID}, {NetId}])");
                        Common_DirtyPorts(packet.PortIds);
                        return false;
                    }
                }
                else
                {
                    NetworkLifecycle.Instance.Server.LogWarning($"Player {player.Username} sent unknown portId: {portNetId}");
                    return false;
                }
            }

        // Only allow the player to update ports on the car they are in/near
        if (player.CarId == packet.NetId)
            return true;

        // Some ports can be updated by the player even if they are not in the car, like doors and windows.
        // Only deny the request if the player is more than 5 meters away from any point of the car.
        if ((player.WorldPosition - transform.position).sqrMagnitude <= CarLengthSq)
            return true;

        NetworkLifecycle.Instance.Server.LogWarning($"Player {player.Username} tried to send a sim flow packet for a car they are not in!");
        Common_DirtyPorts(packet.PortIds);
        return false;
    }

    /// <summary>
    /// Whether a player may write these ports: nobody else holds a lease on any of them. A lease
    /// keeps a lever being held, or the whistle rope, from being fought over. A control that's only
    /// turned or clicked never asks for one (a VR valve, a cab window): requiring a lease refused
    /// every change to those, so the player saw it and nobody else did.
    /// </summary>
    public bool Server_MayWritePorts(ServerPlayer player, IEnumerable<uint> portNetIds)
    {
        if (player == null)
            return false;
        if (NetworkLifecycle.Instance.IsHost(player))
            return true;
        return portNetIds != null && portNetIds.All(portNetId =>
            !portAuthority.TryGetValue(portNetId, out ServerPlayer owner) || owner == null || owner == player);
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

    public void Client_ReceiveControlState(TrainControlStateData state)
    {
        if (state == null || state.Revision < ControlRevision)
            return;
        Client_ReceiveControlCommit(new ClientboundTrainControlCommitPacket
        {
            NetId = NetId,
            Revision = state.Revision,
            Result = TrainControlCommitResult.Accepted,
            PortIds = state.PortIds,
            PortValues = state.PortValues,
            FuseIds = state.FuseIds,
            FuseValues = state.FuseValues,
            IncludesHandbrake = state.HasHandbrake,
            HandbrakePosition = state.HandbrakePosition
        });
    }

    public uint Server_CommitControlRevision()
    {
        if (ControlRevision != uint.MaxValue)
            ControlRevision++;
        return ControlRevision;
    }

    public void Client_SetControlRevision(uint revision)
    {
        if (revision > ControlRevision)
            ControlRevision = revision;
    }

    public void Client_ReceiveControlCommit(ClientboundTrainControlCommitPacket packet)
    {
        if (packet == null)
            return;
        var client = NetworkLifecycle.Instance.Client;
        if (client != null && client.IsOwnTrainControlOperation(packet.OperationId))
        {
            Client_AcknowledgeControlOperation(packet.OperationId);
            // The host didn't take our change; get its state back rather than keep ours.
            if (packet.Result != TrainControlCommitResult.Accepted && !NetworkLifecycle.Instance.IsHost())
                client.SendTrainSyncRequest(NetId);
        }
        // Another player's handbrake operation (a join's state carries no operation).
        else if (packet.IncludesHandbrake && packet.OperationId != 0 &&
                 packet.Result == TrainControlCommitResult.Accepted)
            Common_OnRemoteHandbrakeChange();
        if (packet.Revision < ControlRevision)
            return;

        // Skip controls we have newer changes in flight for: those will commit after this.
        if (packet.PortIds?.Length > 0 && packet.PortValues?.Length == packet.PortIds.Length)
        {
            var (portIds, portValues) = WithoutPending(packet.PortIds, packet.PortValues, client_pendingPortOperations);
            if (portIds.Length > 0)
                Common_UpdatePorts(new CommonTrainPortsPacket
                {
                    NetId = NetId,
                    PortIds = portIds,
                    PortValues = portValues
                });
        }
        if (packet.FuseIds?.Length > 0 && packet.FuseValues?.Length == packet.FuseIds.Length)
        {
            var (fuseIds, fuseValues) = WithoutPending(packet.FuseIds, packet.FuseValues, client_pendingFuseOperations);
            if (fuseIds.Length > 0)
                Common_UpdateFuses(new CommonTrainFusesPacket
                {
                    NetId = NetId,
                    FuseIds = fuseIds,
                    FuseValues = fuseValues
                });
        }
        if (packet.IncludesHandbrake && brakeSystem?.hasHandbrake == true && client_pendingHandbrakeOperation == 0 &&
            Math.Abs(brakeSystem.handbrakePosition - packet.HandbrakePosition) > 0.0001f)
            brakeSystem.SetHandbrakePosition(packet.HandbrakePosition);
        if (packet.BrakeCylinderReleased)
            brakeSystem?.ReleaseBrakeCylinderPressure();
        ControlRevision = packet.Revision;
    }

    // Our operation ids increase monotonically, so a commit for one settles everything we sent
    // up to and including it.
    private void Client_AcknowledgeControlOperation(ulong operationId)
    {
        if (client_pendingHandbrakeOperation != 0 && client_pendingHandbrakeOperation <= operationId)
            client_pendingHandbrakeOperation = 0;
        RemoveSettled(client_pendingPortOperations, operationId);
        RemoveSettled(client_pendingFuseOperations, operationId);

        static void RemoveSettled(Dictionary<uint, ulong> pending, ulong settledUpTo)
        {
            if (pending.Count == 0)
                return;
            foreach (uint id in pending.Where(entry => entry.Value <= settledUpTo).Select(entry => entry.Key).ToList())
                pending.Remove(id);
        }
    }

    private static (uint[] ids, T[] values) WithoutPending<T>(uint[] ids, T[] values, Dictionary<uint, ulong> pending)
    {
        if (pending.Count == 0)
            return (ids, values);
        List<uint> keptIds = new(ids.Length);
        List<T> keptValues = new(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            if (pending.ContainsKey(ids[i]))
                continue;
            keptIds.Add(ids[i]);
            keptValues.Add(values[i]);
        }
        return (keptIds.ToArray(), keptValues.ToArray());
    }

    private void Server_BogieTrackChanged(RailTrack arg1, Bogie arg2)
    {
        BogieTracksDirty = true;
    }

    private void Server_OnCargoLoaded(CargoType obj)
    {
        cargoStateDirty = true;
        cargoIsLoading = true;
    }

    private void Server_OnCargoUnloaded()
    {
        cargoStateDirty = true;
        cargoIsLoading = false;
        CargoModelIndex = byte.MaxValue;
    }

    private void Server_CargoHealthUpdate(float health)
    {
        cargoHealthDirty = true;
    }

    private void Server_CarHealthUpdate(float health)
    {
        //Multiplayer.LogDebug(() => $"Server_CarHealthUpdate({health}) netId: {NetId}");
        carHealthDirty = true;
    }

    private void Server_TrainDamagesHealthUpdate(string field, float health)
    {
        //Multiplayer.LogDebug(() => $"Server_TrainDamagesHealthUpdate({field}, {health}) netId: {NetId}");

        // Check if value has changed before updating
        if (!lastSentTrainDamages.TryGetValue(field, out float lastValue)
            || Mathf.Abs(lastValue - health) > MAX_PORT_DELTA
            || (health == 0 && lastValue != 0)
            || (health == 1 && lastValue != 1))
        {
            lastSentTrainDamages[field] = health;
            carHealthDirty = true;
        }
    }

    private void Server_MainResUpdate(float normalizedPressure, float pressure)
    {
        mainResPressureDirty = true;
    }

    private void Server_BrakeHeatUpdate(bool overheatActive)
    {
        brakeOverheatDirty = true;
    }

    private void Server_CouplerUncoupled(object _, UncoupleEventArgs args)
    {
        sendCouplers |= args.dueToBrokenCouple;
        topologyDirty = true;
    }

    private void Server_CouplerCoupled(object _, CoupleEventArgs args) => topologyDirty = true;

    /// <summary>
    /// The compressor's activation signal follows the brake system's decision (main reservoir
    /// below the threshold), which DV writes to the port only when that decision changes. Once
    /// the port held anything else (in playtest 2 it was 0 with the reservoir empty and the engine
    /// running: no air, so no brakes), nothing put it right. The host keeps them matched.
    /// </summary>
    private void Server_KeepCompressorSignal()
    {
        if (compressorSignalPort == null || TrainCar.brakeSystem == null)
            return;
        float wanted = TrainCar.brakeSystem.compressorActivationSignal ? 1f : 0f;
        if (compressorSignalPort.Value != wanted)
            compressorSignalPort.ExternalValueUpdate(wanted);
    }

    private void Server_OnTick(uint tick)
    {
        if (UnloadWatcher.isUnloading)
            return;

        Server_KeepCompressorSignal();
        Server_SendBrakeStates(tick);
        Server_SendCouplers();
        Server_SendCables();
        Server_SendCargoState();
        Server_SendCargoHealthUpdate();
        Server_SendCarHealthState();

        // Native Coupler events fire before Trainset.Merge/Split and brake-line work completes.
        // Commit on the following tick so the snapshot observes the completed transaction.
        if (topologyDirty)
        {
            topologyDirty = false;
            NetworkLifecycle.Instance.Server?.CommitObservedTrainTopology();
        }

        TicksSinceSync++; //keep track of last full sync
    }

    private void Server_SendBrakeStates(uint tick)
    {
        float main = brakeSystem.mainReservoirPressure;
        float pipe = brakeSystem.brakeset?.pipePressure ?? brakeSystem.brakePipePressure;
        float cylinder = brakeSystem.brakeCylinderPressure;
        bool pressureChanged = float.IsNaN(lastSentMainReservoirPressure) ||
            Math.Abs(main - lastSentMainReservoirPressure) >= 0.02f ||
            Math.Abs(pipe - lastSentBrakePipePressure) >= 0.02f ||
            Math.Abs(cylinder - lastSentBrakeCylinderPressure) >= 0.02f;
        bool heartbeat = unchecked(tick - lastBrakeStateTick) >=
            (uint)(NetworkLifecycle.TICK_RATE * 2);
        if (!mainResPressureDirty && !brakeOverheatDirty && !pressureChanged && !heartbeat)
            return;

        mainResPressureDirty = false;
        brakeOverheatDirty = false;
        lastSentMainReservoirPressure = main;
        lastSentBrakePipePressure = pipe;
        lastSentBrakeCylinderPressure = cylinder;
        lastBrakeStateTick = tick;
        var hc = brakeSystem.heatController;
        NetworkLifecycle.Instance.Server.SendBrakeState
        (
            NetId,
            main, pipe, cylinder,
            hc.overheatPercentage, hc.overheatReductionFactor, hc.temperature
        );
    }

    private void Server_SendCouplers()
    {
        if (!sendCouplers)
            return;

        sendCouplers = false;
        NetworkLifecycle.Instance.Server?.CommitObservedTrainTopology();
    }

    private void Server_SendCables()
    {
        if (!sendCables)
            return;
        sendCables = false;
        NetworkLifecycle.Instance.Server?.CommitObservedTrainTopology();
    }

    private void Server_SendCargoState()
    {
        if (!cargoStateDirty)
            return;
        cargoStateDirty = false;
        if (cargoIsLoading && TrainCar.logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;

        NetworkLifecycle.Instance.Server.SendCargoState(this, cargoIsLoading, CargoModelIndex);
    }

    private void Server_SendCargoHealthUpdate()
    {
        if (!cargoHealthDirty)
            return;

        cargoHealthDirty = false;

        if (TrainCar.logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;

        NetworkLifecycle.Instance.Server.SendCargoHealthUpdate(NetId, TrainCar.CargoDamage.currentHealth);
    }

    private void Server_SendCarHealthState()
    {
        if (!carHealthDirty)
            return;
        carHealthDirty = false;
        NetworkLifecycle.Instance.Server.SendCarHealthUpdate(NetId, TrainCarHealthData.From(TrainCar));
    }

    public bool Server_ValidateCouplerInteraction(CommonCouplerInteractionPacket packet, ServerPlayer player)
    {
        Multiplayer.LogDebug(() =>
                $"Server_ValidateCouplerInteraction([[{(CouplerInteractionType)packet.Flags}], {CurrentID}, {packet.NetId}], {player.PlayerId}) " +
                $"isFront: {packet.IsFrontCoupler}, frontInteracting: {frontInteracting}, frontInteractionPeer: {frontInteractionPlayer}, " +
                $"rearInteracting: {rearInteracting}, rearInteractionPeer: {rearInteractionPlayer}"
                );

        //Ensure no one else is interacting
        if (packet.IsFrontCoupler && frontInteracting && player != frontInteractionPlayer ||
           packet.IsFrontCoupler == false && rearInteracting && player != rearInteractionPlayer)
        {
            Multiplayer.LogDebug(() => $"Server_ValidateCouplerInteraction([{packet.Flags}, {CurrentID}, {packet.NetId}], {player.PlayerId}) Failed to validate!");
            return false;
        }

        Multiplayer.LogDebug(() => $"Server_ValidateCouplerInteraction([{packet.Flags}, {CurrentID}, {packet.NetId}], {player.PlayerId}) No one interacting");

        if (((CouplerInteractionType)packet.Flags).HasFlag(CouplerInteractionType.Start))
        {
            if (packet.IsFrontCoupler)
            {
                frontInteracting = true;
                frontInteractionPlayer = player;
            }
            else
            {
                rearInteracting = true;
                rearInteractionPlayer = player;
            }
        }
        else
        {
            if (packet.IsFrontCoupler)
                frontInteracting = false;
            else
                rearInteracting = false;
        }

        //todo: Additional checks for player location/proximity

        Multiplayer.LogDebug(() => $"Server_ValidateCouplerInteraction([{packet.Flags}, {CurrentID}, {packet.NetId}], {player.PlayerId}) Validation passed!");
        return true;
    }

    public void Server_ReceiveAuthorityRequest(uint portNetId, ServerPlayer player, bool requestAuthority)
    {
        using IDisposable debugScope = DebugTrace.BeginApply("train", "train.authority-apply", DebugRuntimeSide.Server, "Control/Port", $"{NetId}:{portNetId}",
            () => DebugValueSnapshotter.SnapshotObject(new { carNetId = NetId, portNetId, playerId = player?.PlayerId, requestAuthority }));
        portAuthority.TryGetValue(portNetId, out var currentAuth);

        if (requestAuthority)
        {
            if (currentAuth == null)
            {
                if ((player.WorldPosition - transform.position).sqrMagnitude > CarLengthSq)
                {
                    DebugTrace.Validation("train", "Control/Port", $"{NetId}:{portNetId}", false, "sender-out-of-range", DebugRuntimeSide.Server);
                    NetworkLifecycle.Instance.Server.LogWarning($"Player \"{player.Username}\" attempted to gain authority for a control on car {CurrentID}, but they are too far away!");
                    NetworkLifecycle.Instance.Server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Denied, player);
                    NetworkLifecycle.Instance.Server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Released, player);
                    return;
                }

                // No authority exists (or cleanup failed) - grant authority and communicate to all players
                NetworkLifecycle.Instance.Server.LogDebug(() => $"Player \"{player.Username}\" granted authority for a control on car {CurrentID}");
                portAuthority[portNetId] = player;
                NetworkLifecycle.Instance.Server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Blocked, excludePlayer: player);
            }
            else if (currentAuth != player)
            {
                DebugTrace.Validation("train", "Control/Port", $"{NetId}:{portNetId}", false, "authority-held-by-other-player", DebugRuntimeSide.Server);
                NetworkLifecycle.Instance.Server.LogWarning($"Player \"{player.Username}\" attempted to gain authority for a control that's in use on car {CurrentID}");
                NetworkLifecycle.Instance.Server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Denied, player);
            }
        }
        else
        {
            // Release request
            if (currentAuth == player)
            {
                NetworkLifecycle.Instance.Server.LogDebug(() => $"Player \"{player.Username}\" released authority for a control on car {CurrentID}");
                portAuthority.Remove(portNetId);
                NetworkLifecycle.Instance.Server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Released);
            }
            else if (currentAuth != null)
            {
                DebugTrace.Validation("train", "Control/Port", $"{NetId}:{portNetId}", false, "authority-release-not-owner", DebugRuntimeSide.Server);
                NetworkLifecycle.Instance.Server.LogWarning($"Player \"{player.Username}\" attempted to release authority for a control that's not theirs on car {CurrentID}");
                NetworkLifecycle.Instance.Server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Denied, player);
            }
        }
    }

    public void Server_ValidatePaintThemeChange(TrainCarPaint.Target target, PaintTheme theme, ServerPlayer player)
    {
        var playerPos = TrainCar.transform.InverseTransformPoint(player.WorldPosition);
        Vector3 localClosestPoint = TrainCar.Bounds.ClosestPoint(playerPos);
        Vector3 worldClosestPoint = TrainCar.transform.TransformPoint(localClosestPoint);

        if ((player.WorldPosition - worldClosestPoint).sqrMagnitude > MAX_PAINT_DISTANCE_SQ)
        {
            NetworkLifecycle.Instance.Server.LogWarning($"Player \"{player.Username}\" attempted to change the paint theme for car {CurrentID}, but they are too far away!");

            var paintControllers = GetComponents<TrainCarPaint>();
            int i = 0;
            while (i < paintControllers.Length)
            {
                if (paintControllers[i].TargetArea == target)
                    break;

                i++;
            }

            if (i >= paintControllers.Length)
                return;

            if (!PaintThemeLookup.Instance.TryGetNetId(paintControllers[i].CurrentTheme, out var themeNetId))
            {
                Multiplayer.LogWarning($"ValidatePaintThemeChange could not find themeNetId for theme {paintControllers[i].CurrentTheme.AssetName} on [{CurrentID}, {NetId}]");
                return;
            }

            NetworkLifecycle.Instance.Server.SendPaintThemeChange(this, target, themeNetId, player);

            return;
        }

        Common_ReceivePaintThemeUpdate(target, theme);
    }

    private void Server_OnPlayerDisconnect(ServerPlayer player)
    {
        //todo: resolve player disconnection during chain interaction
        if (frontInteractionPlayer == player || rearInteractionPlayer == player)
        {
            Multiplayer.LogWarning($"Server_OnPlayerDisconnect() Coupler interaction in unknown state [{CurrentID}, {NetId}] isFront: {frontInteractionPlayer == player}");
            if (frontInteractionPlayer == player)
            {
                frontInteracting = false;
                //NetworkLifecycle.Instance.Client.SendCouplerInteraction(cou, coupler, otherCoupler);
            }
            else
            {
                rearInteracting = false;
            }
        }

        // Clean up blocked controls
        foreach (var kvp in portAuthority.Where(kvp => kvp.Value == player).ToArray())
        {
            portAuthority.Remove(kvp.Key);
            NetworkLifecycle.Instance.Server.SendTrainControlAuthorityUpdate(NetId, kvp.Key, ControlAuthorityState.Released);
        }
    }
    #endregion

    #region Common

    private void Common_OnTick(uint tick)
    {
        if (UnloadWatcher.isUnloading)
            return;

        Common_SendHandbrakePosition();
        Common_SendFuses();
        Common_SendPorts();
        Common_SendPaintThemes();
    }

    /// <summary>
    /// Whether the local player is close enough to have changed this car's controls by hand: the
    /// same distance the host checks. A client's own simulation also sets handbrakes and fuses
    /// (notably while the world loads); reporting those for distant cars only produced rejected
    /// operations and resync requests. The host always reports.
    /// </summary>
    private bool Client_LocalPlayerCanTouch() =>
        NetworkLifecycle.Instance.IsHost() ||
        (PlayerManager.PlayerTransform != null &&
         (PlayerManager.PlayerTransform.position - transform.position).sqrMagnitude <= CarLengthSq);

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
        if (!viaRemote && !Client_LocalPlayerCanTouch())
            return; // simulation noise (e.g. set during load), not a player action
        ulong operationId = NetworkLifecycle.Instance.Client.SendHandbrakePositionChanged(NetId, brakeSystem.handbrakePosition, viaRemote);
        lastHandbrakeSentAt = Time.realtimeSinceStartup;
        if (!NetworkLifecycle.Instance.IsHost())
            client_pendingHandbrakeOperation = operationId;
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

    private void Common_SendPorts()
    {
        if (!hasSimFlow || dirtyPorts.Count == 0)
            return;

        uint[] portIds;
        List<uint> settling = null;
        if (NetworkLifecycle.Instance.IsHost())
            portIds = dirtyPorts.Where(id => !locallyFedPorts.Contains(id)).ToArray(); // a full resync dirties every port
        else
        {
            List<uint> send = new(dirtyPorts.Count);
            foreach (uint id in dirtyPorts)
            {
                UnleasedChange change = locallyOwnedPortAuthorities.Contains(id) ? UnleasedChange.Send : Client_UnleasedChange(id);
                if (change == UnleasedChange.Send)
                    send.Add(id);
                else if (change == UnleasedChange.Settling)
                    (settling ??= new()).Add(id);
            }
            portIds = send.ToArray();
        }
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
                if (!NetworkLifecycle.Instance.IsHost())
                    client_hostPortValues[portNetId] = value;
            }
            else
            {
                Multiplayer.LogWarning($"Failed to send port \"{portId}\" for [{CurrentID}, {NetId}]");
            }

            i++;
        }

        ulong operationId = NetworkLifecycle.Instance.Client.SendPorts(NetId, portIds, portValues);
        if (!NetworkLifecycle.Instance.IsHost())
            foreach (uint portNetId in portIds)
                client_pendingPortOperations[portNetId] = operationId;
    }

    // A control the player changed without anything taking its lease: a valve, lever or cylinder
    // cock turned in VR (they don't all raise Grabbed), or a control on the car body like a cab
    // window or an oiling point, which isn't hooked at all. The client dropped these as feedback, so
    // only the player saw the change (#73 covered buttons and switches, which raise Used). The host
    // takes a change nobody holds a lease on. Only controls players drive count (ControlPortWriters):
    // DV and mods set some CONTROL ports from the simulation. Feedback is told apart by its value:
    // once settled it's what the host last sent.
    private UnleasedChange Client_UnleasedChange(uint portNetId)
    {
        if (!hasSimFlow || portWriters == null || !simulationFlow.TryGetPort(GetPort(portNetId), out Port port))
            return UnleasedChange.No;
        bool blocked = portNetIdToBlockState.TryGetValue(portNetId, out bool isBlocked) && isBlocked;
        float? hostValue = client_hostPortValues.TryGetValue(portNetId, out float value) ? value : null;
        float? sinceHostValue = client_hostPortReceivedAt.TryGetValue(portNetId, out float receivedAt)
            ? Time.realtimeSinceStartup - receivedAt : null;
        return portWriters.Unleased(portNetId, blocked, Client_LocalPlayerCanTouch(), port.Value, hostValue, sinceHostValue,
            UNLEASED_CHANGE_TOLERANCE, UNLEASED_CHANGE_SETTLE_SECONDS);
    }

    private void Common_SendFuses()
    {
        if (!hasSimFlow || dirtyFuses.Count == 0)
            return;
        if (!Client_LocalPlayerCanTouch())
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

        ulong operationId = NetworkLifecycle.Instance.Client.SendFuses(NetId, fuseIds, fuseValues);
        if (!NetworkLifecycle.Instance.IsHost())
            foreach (uint fuseNetId in fuseIds)
                client_pendingFuseOperations[fuseNetId] = operationId;
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

            if (NetworkLifecycle.Instance.IsHost())
                NetworkLifecycle.Instance.Server.SendPaintThemeChange(this, paintController.targetArea, themeNetId);
            else
                NetworkLifecycle.Instance.Client.SendPaintThemeChange(this, paintController.targetArea, themeNetId);
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
        NetworkLifecycle.Instance.Client.SendBrakeCylinderReleased(NetId);
    }

    private void Common_OnPortUpdated(Port port, uint portNetId)
    {
        DebugRuntime.PublishHighFrequency("train", "train.port-value-changed", NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            "Control/Port", $"{NetId}:{portNetId}",
            () => DebugValueSnapshotter.SnapshotObject(new { carNetId = NetId, portNetId, port.id, port.Value, port.prevValue, port.valueType }));
        //Multiplayer.LogDebug(() => $"Common_OnPortUpdated() port [{port?.id}] updated on [{CurrentID}, {NetId}]. Value: {port?.Value}, PrevValue: {port?.prevValue}, ValueType: {port?.valueType}, Tick: {NetworkLifecycle.Instance.Tick}");
        if (port.valueType != PortValueType.CONTROL && !NetworkLifecycle.Instance.IsHost())
        {
            Multiplayer.LogDebug(() => $"Common_OnPortUpdated() Ignoring non-control port update for [{port.id}] on [{CurrentID}, {NetId}]");
            return;
        }
        // CONTROL ports simulation code sets (the compressor's signal, a horn fed from its control,
        // a gearbox's gear input) are the host's: a client sends only the ones players drive.
        if (!NetworkLifecycle.Instance.IsHost() && portWriters?.ClientReports(portNetId) != true)
            return;

        if (UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket)
            return;
        if (float.IsNaN(port.prevValue) && float.IsNaN(port.Value))
            return;

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
                if (portNetIdToBlockState.TryGetValue(portNetId, out var isBlocked) && isBlocked)
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

    public void Common_UpdatePorts(CommonTrainPortsPacket packet)
    {
        if (!hasSimFlow)
            return;

        for (int i = 0; i < packet.PortIds.Length; i++)
        {
            if (locallyFedPorts.Contains(packet.PortIds[i])) // an older host still sends them
                continue;
            var portId = GetPort(packet.PortIds[i]);
            if (simulationFlow.TryGetPort(portId, out Port port))
            {
                float value = packet.PortValues[i];
                if (!NetworkLifecycle.Instance.IsHost())
                {
                    client_hostPortValues[packet.PortIds[i]] = value;
                    client_hostPortReceivedAt[packet.PortIds[i]] = Time.realtimeSinceStartup;
                }

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

    private IEnumerator Client_InitLater()
    {
        while ((client_bogie1Queue = bogie1.GetComponent<NetworkedBogie>()) == null)
            yield return null;
        while ((client_bogie2Queue = bogie2.GetComponent<NetworkedBogie>()) == null)
            yield return null;

        Client_Initialized = true;
    }

    private float nextDerailResyncRequestAt;

    // Put a car the host has on the rails back on the host's track where the host has it. That
    // needs a full sync (pose and track); ask for one until it comes. Rerailing uncouples the car
    // locally (we're applying a host packet, so that isn't reported), so restore the coupling from
    // the host's topology once the rerail has finished.
    private void Client_RerailToHost(in TrainsetMovementPart movementPart)
    {
        if (TrainCar.IsTeleporting)
            return;
        BogieData bogie = movementPart.Bogie1.IncludesTrackData ? movementPart.Bogie1 : movementPart.Bogie2;
        if (!movementPart.typeFlag.HasFlag(TrainsetMovementPart.MovementType.Position) || !bogie.IncludesTrackData ||
            !global::Multiplayer.Components.Networking.World.NetworkedRailTrack.TryGet(bogie.TrackNetId, out global::Multiplayer.Components.Networking.World.NetworkedRailTrack track) || track.RailTrack == null)
        {
            if (Time.realtimeSinceStartup >= nextDerailResyncRequestAt)
            {
                nextDerailResyncRequestAt = Time.realtimeSinceStartup + 2f;
                NetworkLifecycle.Instance.Client?.SendTrainSyncRequest(NetId);
            }
            return;
        }

        Multiplayer.LogWarning($"{CurrentID} derailed here but not on the host; rerailing it to the host's track");
        Client_BeginRelocation();
        TrainCar.Rerail(track.RailTrack, movementPart.Position + WorldMover.currentMove,
            movementPart.Rotation * Vector3.forward);
        Client_FinishRelocationLater();
        CoroutineManager.Instance.StartCoroutine(RestoreTopologyAfterRerail());
    }

    /// <summary>The host rerailed this car (<see cref="TrainCar.Rerail"/>).</summary>
    public void Client_RerailFromHost(RailTrack track, Vector3 worldPos, Vector3 forward)
    {
        if (!TrainCar.derailed)
            return;
        Client_BeginRelocation();
        TrainCar.Rerail(track, worldPos, forward);
        Client_FinishRelocationLater();
    }

    /// <summary>The host moved this car to another track (<see cref="TrainCar.MoveToTrackWithCarUncouple"/>).</summary>
    public void Client_MoveToTrackFromHost(RailTrack track, Vector3 worldPos, Vector3 forward)
    {
        if (TrainCar.derailed)
            return;
        Client_BeginRelocation();
        TrainCar.MoveToTrackWithCarUncouple(track, worldPos, forward);
        Client_FinishRelocationLater();
    }

    /// <summary>
    /// Everything that follows the host's samples still aims at where the car was before a rerail
    /// or move: the rigidbody queue at its derailed pose, the bogie queues at their old spans (a
    /// stopped car's sample never goes stale). Left in place, after a rerail the rigidbody queue
    /// dragged the body off at 1.5 m/s, the speed queue placed it back kinematically towards the
    /// old sample, and every resync snapped it over 5 m and flung it, forever (playtest 2, FEF-3).
    /// Vanilla's rerail must also act on a dynamic body: one resting derailed at the host's pose
    /// is kinematic (see <see cref="UpdateDerailedKinematic"/>).
    /// </summary>
    private void Client_BeginRelocation()
    {
        Client_ResetFollowers();
        if (TrainCar.rb != null && TrainCar.rb.isKinematic)
            TrainCar.rb.isKinematic = false;
    }

    private void Client_ResetFollowers()
    {
        Client_trainSpeedQueue?.Clear();
        Client_trainRigidbodyQueue?.Clear();
        client_bogie1Queue?.Clear();
        client_bogie2Queue?.Clear();
        kinematicCycles = 0;
        TrainCar?.stress?.ResetTrainStress();
    }

    /// <summary>
    /// Vanilla relocates the car a physics step later (a coroutine); samples arriving meanwhile
    /// are ignored (see <see cref="Client_ReceiveTrainPhysicsUpdate"/>), so once it's done, reset
    /// again and ask the host for the car's pose and bogie tracks.
    /// </summary>
    private void Client_FinishRelocationLater() => CoroutineManager.Instance.StartCoroutine(Client_FinishRelocation());

    private IEnumerator Client_FinishRelocation()
    {
        for (float t0 = Time.realtimeSinceStartup; TrainCar != null && TrainCar.IsTeleporting && Time.realtimeSinceStartup - t0 < 5f;)
            yield return null;
        yield return new WaitForFixedUpdate();
        if (this == null || TrainCar == null)
            yield break;
        Client_ResetFollowers();
        NetworkLifecycle.Instance.Client?.SendTrainSyncRequest(NetId);
    }

    private System.Collections.IEnumerator RestoreTopologyAfterRerail()
    {
        for (float t0 = Time.realtimeSinceStartup; TrainCar != null && TrainCar.IsTeleporting && Time.realtimeSinceStartup - t0 < 5f;)
            yield return null;
        yield return new WaitForFixedUpdate();
        NetworkLifecycle.Instance.Client?.RestoreCanonicalTrainTopology();
    }

    /// <param name="hostSetSize">How many cars the host sent this car with: its trainset there.</param>
    public bool Client_ReceiveTrainPhysicsUpdate(in TrainsetMovementPart movementPart, uint tick, int hostSetSize = 1)
    {
        using IDisposable debugScope = DebugTrace.BeginApply("train", "train.physics-apply", DebugRuntimeSide.Client, "TrainCar", NetId.ToString(), () =>
        {
            EntityDebugRegistry.RegisterTrain(this);
            return EntityDebugRegistry.Get("TrainCar", NetId.ToString())?.LatestState;
        }, highFrequency: true);
        if (DebugRuntime.EnabledFor("train") && DebugRuntime.ShouldEmitHighFrequency("TrainCar", NetId.ToString()))
            DebugRuntime.Publish("train", "train.physics-received", DebugRuntimeSide.Client, entityType: "TrainCar", entityId: NetId.ToString(),
                data: DebugValueSnapshotter.SnapshotObject(movementPart), highFrequency: true, samplingDecided: true);
        if (!Client_Initialized)
            return false;

        // Mid rerail or move (vanilla takes a physics step): the sample is for the car's new spot,
        // ours isn't there yet. Client_FinishRelocation asks for a fresh one.
        if (TrainCar.IsTeleporting)
            return false;

        if (tick <= lastTickProcessed)
        {
            //Multiplayer.LogWarning($"Received physics update for car {CurrentID} at tick {tick}, but last tick processed was {lastTickProcessed}");
            return false;
        }

        lastTickProcessed = tick;

        bool becameDerailed = false;
        if (movementPart.typeFlag == TrainsetMovementPart.MovementType.RigidBody)
        {
            //Vector3 expectedPosition = movementPart.RigidbodySnapshot.Position + WorldMover.currentMove;
            //Multiplayer.LogDebug(() => $"Processing derailed physics for car {CurrentID} at tick {tick}, current position: {TrainCar.transform.position} expected position: {expectedPosition}");

            if (TrainCar.isEligibleForSleep)
                TrainCar.ForceOptimizationState(false);

            // Derailment is a state transition, not a per-snapshot operation. Re-running
            // the native transition for every rigidbody sample can recreate joints and
            // secondary effects while an already-derailed consist is still moving.
            becameDerailed = !TrainCar.derailed;
            if (becameDerailed)
            {
                // Vanilla's derail must act on a dynamic body: stop any placement first. The bogie
                // samples are for the rails it just left; a rerail must not find them.
                Client_trainSpeedQueue.Clear();
                client_bogie1Queue.Clear();
                client_bogie2Queue.Clear();
                if (TrainCar.rb.isKinematic)
                    TrainCar.rb.isKinematic = false;
                kinematicCycles = 0;
                TrainCar.Derail();
            }
            Client_trainRigidbodyQueue.ReceiveSnapshot(movementPart.RigidbodySnapshot, tick);

            //Multiplayer.LogDebug(() => $"Derailed car {TrainCar.ID} positioned at {TrainCar.transform.position}");
        }
        else
        {
            // Derailed here but on the rails on the host: a client-only derail (a physics hitch).
            // Bogie samples are ignored while derailed, so it used to stay derailed for good.
            if (TrainCar.derailed)
            {
                // A derailed car resting at the host's pose is kinematic (see UpdateDerailedKinematic);
                // vanilla's rerail must act on a dynamic body.
                if (TrainCar.rb != null && TrainCar.rb.isKinematic)
                    TrainCar.rb.isKinematic = false;
                kinematicCycles = 0;
                Client_RerailToHost(movementPart);
                return false;
            }

            // Only force awake if there's movement
            if (Mathf.Abs(movementPart.Speed) > NetworkTrainsetWatcher.VELOCITY_THRESHOLD && TrainCar.isEligibleForSleep)
                TrainCar.ForceOptimizationState(false);

            //move the car to the correct position first - maybe?
            if (movementPart.typeFlag.HasFlag(TrainsetMovementPart.MovementType.Position))
            {
                // Project the host's pose to now so a moving car isn't compared against the past.
                float ageSeconds = Mathf.Clamp((NetworkLifecycle.Instance.Tick - (float)tick) / NetworkLifecycle.TICK_RATE, 0f, 0.5f);
                Vector3 worldPos = movementPart.Position + WorldMover.currentMove +
                                   movementPart.Rotation * Vector3.forward * (movementPart.Speed * ageSeconds);

                float positionDelta = Vector3.Distance(TrainCar.rb != null ? TrainCar.rb.position : TrainCar.transform.position, worldPos);

                if (positionDelta > POSITION_UPDATE_THRESHOLD)
                {
                    if (TrainCar.rb != null)
                    {
                        // Move the bogies with the body and stop everything: moving the body
                        // alone left the bogies to drag it back, and kept whatever speed the car
                        // had built up locally (the snap then "bounced").
                        Vector3 shift = worldPos - TrainCar.rb.position;
                        TrainCar.rb.MovePosition(worldPos);
                        TrainCar.rb.velocity = Vector3.zero;
                        TrainCar.rb.angularVelocity = Vector3.zero;
                        if (TrainCar.Bogies != null)
                        {
                            foreach (Bogie bogie in TrainCar.Bogies)
                            {
                                if (bogie == null || bogie.HasDerailed || bogie.rb == null)
                                    continue;
                                bogie.rb.MovePosition(bogie.rb.position + shift);
                                bogie.rb.velocity = Vector3.zero;
                                bogie.rb.angularVelocity = Vector3.zero;
                            }
                        }

                        //TrainCar.rb.MoveRotation(movementPart.Rotation); // removed due to motion sickness issues
                    }

                    //clear the queues?
                    Client_trainSpeedQueue.Clear();
                    Client_trainRigidbodyQueue.Clear();
                    client_bogie1Queue.Clear();
                    client_bogie2Queue.Clear();

                    TrainCar.stress.ResetTrainStress();
                }
            }

            if (Client_trainSpeedQueue is TrainSpeedQueue speedQueue)
                speedQueue.HostCoupled = hostSetSize > 1;
            Client_trainSpeedQueue.ReceiveSnapshot(movementPart.Speed, tick);
            TrainCar.stress.slowBuildUpStress = movementPart.SlowBuildUpStress;
            client_bogie1Queue.ReceiveSnapshot(movementPart.Bogie1, movementPart.Speed, tick);
            client_bogie2Queue.ReceiveSnapshot(movementPart.Bogie2, movementPart.Speed, tick);
        }

        // Only write isKinematic when it changes: the write itself disturbs the body. On-rail cars
        // are dynamic except while TrainSpeedQueue slides one into place.
        if (movementPart.typeFlag == TrainsetMovementPart.MovementType.RigidBody)
            UpdateDerailedKinematic(movementPart.RigidbodySnapshot);
        else if (TrainCar.rb.isKinematic && !(Client_trainSpeedQueue is TrainSpeedQueue { Kinematic: true }))
            TrainCar.rb.isKinematic = false;

        return becameDerailed;
    }

    private const float DERAILED_KINEMATIC_SPEED = 0.3f;    // m/s: the host's derailed car is nearly still ...
    private const float DERAILED_DYNAMIC_SPEED = 0.5f;      // ... and moving again above this
    private const float DERAILED_KINEMATIC_DISTANCE = 0.3f; // m and degrees our car must be within of the host's
    private const float DERAILED_KINEMATIC_DEGREES = 10f;

    /// <summary>
    /// A derailed car the host has come to rest (it never quite stops: handbrakes don't act on
    /// derailed bogies) is made kinematic at the host's pose once ours has reached it. As a
    /// dynamic body moved there every step it was a bulldozer: after a pile-up that went a little
    /// differently here it kept shoving the cars around it off their places.
    ///
    /// With no world loaded here under the car (this player is far from it), there's nothing for a
    /// dynamic copy to land on: it fell, was snapped back at 20 m and fell again, endlessly. It
    /// stays kinematic there instead, and the rigidbody queue places it at the host's pose. When
    /// the world loads, DV's DerailedTrainCarIsKinematicHandler releases it, and the rules above
    /// apply again.
    /// </summary>
    private void UpdateDerailedKinematic(RigidbodySnapshot snapshot)
    {
        Rigidbody rb = TrainCar.rb;
        if (snapshot == null || rb == null)
            return;
        if (WorldStreamingInit.Instance != null && !WorldStreamingInit.Instance.IsSceneAndTerrainCellLoaded(rb.position))
        {
            kinematicCycles = 0;
            if (!rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
            return;
        }
        var flags = (RigidbodySnapshot.IncludedData)snapshot.IncludedDataFlags;
        float hostSpeed = flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity) ? snapshot.Velocity.magnitude : 0f;
        if (hostSpeed > DERAILED_DYNAMIC_SPEED)
        {
            kinematicCycles = 0;
            if (rb.isKinematic)
                rb.isKinematic = false;
            return;
        }
        if (rb.isKinematic || hostSpeed > DERAILED_KINEMATIC_SPEED || !flags.HasFlag(RigidbodySnapshot.IncludedData.Position))
            return;
        bool atPose = Vector3.Distance(rb.position, snapshot.Position + WorldMover.currentMove) < DERAILED_KINEMATIC_DISTANCE &&
                      (!flags.HasFlag(RigidbodySnapshot.IncludedData.Rotation) ||
                       Quaternion.Angle(rb.rotation, snapshot.Rotation) < DERAILED_KINEMATIC_DEGREES);
        if (!atPose)
        {
            kinematicCycles = 0;
            return;
        }
        if (++kinematicCycles >= MIN_KINEMATIC_CYCLES)
            rb.isKinematic = true;
    }

    public bool Client_ReceiveBrakeStateUpdate(ClientboundBrakeStateUpdatePacket packet)
    {
        if (brakeSystem == null)
            return true;

        if (!hasSimFlow)
            return true;

        brakeSystem.SetMainReservoirPressure(packet.MainReservoirPressure);

        bool lineApplied = TrainTopologyNativeAdapter.TryApplyBrakeLine(new BrakeLineStateData
        {
            AnchorCarNetId = packet.BrakeLineAnchorCarNetId,
            AnchorEnd = TrainEnd.Rear,
            MemberDigest = packet.BrakeLineMemberDigest,
            PipePressure = packet.BrakePipePressure
        });

        brakeSystem.brakeCylinderPressure = packet.BrakeCylinderPressure;

        if (brakeSystem.heatController == null)
            return lineApplied;

        brakeSystem.heatController.overheatPercentage = packet.OverheatPercent;
        brakeSystem.heatController.overheatReductionFactor = packet.OverheatReductionFactor;
        brakeSystem.heatController.temperature = packet.Temperature;
        return lineApplied;
    }

    private void Client_OnIgnite(float ignition)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket)
            return;

        if (ignition == 0f)
            return;

        NetworkLifecycle.Instance.Client.LogDebug(() => $"Common_OnIgnite({CurrentID})");
        NetworkLifecycle.Instance.Client.SendFireboxIgnition(NetId);
    }

    public void Client_CouplerStateChange(ChainCouplerInteraction.State state, Coupler coupler)
    {
        //Multiplayer.LogDebug(() => $"1 Client_CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}], coupler is front: {coupler?.isFrontCoupler}");

        //if we are processing a packet, then these state changes are likely triggered by a received update, not player interaction
        //in future, maybe patch OnGrab() or add logic to add/remove action subscriptions
        if (NetworkLifecycle.Instance.IsProcessingPacket || IsAuthoritativeTopologyObservationSuppressed)
            return;

        CouplerInteractionType interactionFlags = CouplerInteractionType.NoAction;
        Coupler otherCoupler = null;

        switch (state)
        {
            case ChainCouplerInteraction.State.Being_Dragged:
                couplerInteraction = coupler;
                localChainDrags.Add(coupler);
                originalState = coupler.state;
                originalCoupledTo = coupler.coupledTo;
                // Dragging is presentation local to the manipulating player. Sending the
                // native FSM's transient states made the host and topology reconciler race
                // one another. Only the completed placement is committed below.
                //Multiplayer.LogDebug(() => $"3 Client_CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}]");
                break;

            case ChainCouplerInteraction.State.Attached_Loose:
                if (couplerInteraction != null)
                {
                    //couldn't find an appropriate constant in the game code, other than the default value
                    //at B99.3 this distance is 1.5f for both default and constant/magic number
                    // Native coupling has normally populated coupledTo before the state
                    // callback fires. Prefer that canonical relationship; the spatial
                    // range query can already be empty after the joint snaps the cars.
                    otherCoupler = coupler.coupledTo ?? coupler.GetFirstCouplerInRange();
                    //Multiplayer.LogDebug(() => $"4 Client_CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}] coupledTo: {coupler?.coupledTo?.train?.ID}, first Coupler: {otherCoupler?.train?.ID}");
                    interactionFlags = CouplerInteractionType.CouplerCouple;
                }
                break;

            case ChainCouplerInteraction.State.Parked:
                if (couplerInteraction != null)
                {
                    NetworkLifecycle.Instance.Client.SendChainPlacement(coupler,
                        TrainChainState.Parked, originalCoupledTo != null);
                    couplerInteraction = null;
                }
                break;

            case ChainCouplerInteraction.State.Dangling:
                if (couplerInteraction != null)
                {
                    NetworkLifecycle.Instance.Client.SendChainPlacement(coupler,
                        TrainChainState.Dangling, originalCoupledTo != null);
                    couplerInteraction = null;
                }
                break;

            case ChainCouplerInteraction.State.Attached_Tight:
            case ChainCouplerInteraction.State.Other_Attached_Parked:
            case ChainCouplerInteraction.State.Other_Attached_Dangling:
                // A drop that settled in a final state not handled above (e.g. the other side's chain
                // was attached): nothing to report, but don't leave the interaction open; take the
                // host's state back instead.
                if (couplerInteraction == coupler)
                {
                    couplerInteraction = null;
                    NetworkLifecycle.Instance.Client?.SendTrainSyncRequest(NetId);
                }
                break;

            default:
                //nothing to do
                break;
        }

        if (interactionFlags != CouplerInteractionType.NoAction)
        {
            //Multiplayer.LogDebug(() => $"8 Client_CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}], coupler is front: {coupler?.isFrontCoupler}, Sending: {interactionFlags}");
            NetworkLifecycle.Instance.Client.SendCouplerInteraction(interactionFlags, coupler, otherCoupler);

            //finished interaction, clear flag
            if (interactionFlags != CouplerInteractionType.Start)
                couplerInteraction = null;

            return;
        }
        //Multiplayer.LogDebug(() => $"9 Client_CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}]");
    }

    private void Client_ControlGrabbed(ControlImplBase control)
    {
        Multiplayer.LogDebug(() => $"Client_ControlGrabbed() Control {control.name}, car: {CurrentID}");
        if (!controlToPortNetId.TryGetValue(control, out var portNetId))
        {
            Multiplayer.LogWarning($"Control \"{control.name}\" grabbed but netId not found on TrainCar \"{CurrentID}\", hash: {control.GetHashCode()}, instance: {control.GetInstanceID()}");
            return;
        }

        if (portNetIdToBlockState.TryGetValue(portNetId, out var isBlocked) && isBlocked)
        {
            Multiplayer.LogDebug(() => $"Client_ControlGrabbed() Control [{control.name}, {portNetId}] is blocked on car {CurrentID}, ending interaction");
            control.ForceEndInteraction();
        }
        else
        {
            Multiplayer.LogDebug(() => $"Client_ControlGrabbed() Control [{control.name}, {portNetId}] is not blocked on car {CurrentID}, requesting authority");
            foreach (uint leased in PortsOf(portNetId))
            {
                locallyOwnedPortAuthorities.Add(leased);
                NetworkLifecycle.Instance.Client?.SendTrainControlAuthorityRequest(NetId, leased, true);
            }
        }
    }

    // A control's port and any more it feeds (the C Class's bright switch feeds the front and the
    // rear headlights): they're leased and released together.
    private IEnumerable<uint> PortsOf(uint portNetId)
    {
        yield return portNetId;
        if (linkedPortNetIds.TryGetValue(portNetId, out uint[] linked))
            foreach (uint other in linked)
                yield return other;
    }

    private void Client_ControlScrolled(InteriorControlsManager.ControlReference controlRef)
    {
        if (controlRef.controlImplBase.IsHoverScrolled())
        {
            // Currently scrolling
            Multiplayer.LogDebug(() => $"Client_ControlScrolled() Control {controlRef.overridableBaseControl.ControlType} on car {CurrentID} was scrolled while hovered, requesting authority");

            if (!scrollingControls.ContainsKey(controlRef.scrollable))
            {
                scrollingControls[controlRef.scrollable] = StartCoroutine(Client_CheckScrolling(controlRef));
                Client_ControlGrabbed(controlRef.controlImplBase);
            }
        }
    }

    // A press without a grab: a VR button or toggle switch (they never raise Grabbed), or a keyboard
    // shortcut. Used fires before the control changes its value, so leasing here means the change is
    // sent; the lease is handed back once it has gone out, as for a released grab.
    private void Client_ControlUsed(ControlImplBase control)
    {
        if (control.IsGrabbed() || !controlToPortNetId.TryGetValue(control, out var portNetId))
            return;

        if (!locallyOwnedPortAuthorities.Contains(portNetId))
            Client_ControlGrabbed(control);

        if (usedControlReleases.TryGetValue(control, out var pending) && pending != null)
            StopCoroutine(pending);
        usedControlReleases[control] = StartCoroutine(Client_ReleaseUsedControl(control));
    }

    private IEnumerator Client_ReleaseUsedControl(ControlImplBase control)
    {
        // Past the next tick, which sends the change.
        yield return new WaitForSecondsRealtime(USED_CONTROL_LEASE_SECONDS);
        usedControlReleases.Remove(control);
        if (control != null && !control.IsGrabbed())
            Client_ControlUngrabbed(control);
    }

    private IEnumerator Client_CheckScrolling(InteriorControlsManager.ControlReference controlRef)
    {
        // Wait for player to stop scrolling
        while (controlRef.controlImplBase.IsHoverScrolled())
            yield return new WaitForSecondsRealtime(0.5f);

        Multiplayer.LogDebug(() => $"Client_CheckScrolling() Control {controlRef.overridableBaseControl.ControlType} on car {CurrentID} scrolling complete");
        scrollingControls.Remove(controlRef.scrollable);
        Client_ControlUngrabbed(controlRef.controlImplBase);
    }

    private void Client_ControlUngrabbed(ControlImplBase control)
    {
        Multiplayer.LogDebug(() => $"Client_ControlUngrabbed() Control {control.name}, car: {CurrentID}");
        if (!controlToPortNetId.TryGetValue(control, out var portNetId))
        {
            Multiplayer.LogWarning($"Control \"{control.name}\" ungrabbed but netId not found on TrainCar \"{CurrentID}\"");
            return;
        }

        if (!portNetIdToBlockState.ContainsKey(portNetId))
            portNetIdToBlockState[portNetId] = false;

        if (portNetIdToBlockState.TryGetValue(portNetId, out var isBlocked) && !isBlocked)
        {
            var hinge = control.GetComponent<HingeJoint>();
            var cj = control.GetComponent<ConfigurableJoint>();
            if (hinge != null && hinge.useSpring || cj != null)
            {
                StartCoroutine(WaitForControlToSettle(control, portNetId));
                return;
            }
            Multiplayer.LogDebug(() => $"Client_ControlUngrabbed() Control [{control.name}, {portNetId}] not blocked, releasing authority for car {CurrentID}");
            Client_ReleasePortAuthority(portNetId);
        }
    }

    private IEnumerator WaitForControlToSettle(ControlImplBase control, uint portNetId)
    {
        var time = Time.time;

        var oldValue = float.MaxValue;

        try
        {
            while (!Mathf.Approximately(control.Value, oldValue))
            {
                oldValue = control.Value;
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }
        finally
        {
            Multiplayer.LogDebug(() => $"WaitForControlToSettle() Control [{control.name}, {portNetId}], releasing authority for car {CurrentID} after {Time.time - time}");
            Client_ReleasePortAuthority(portNetId);
        }
    }

    private void Client_ReleasePortAuthority(uint portNetId)
    {
        // Authority requests and port commits share ReliableOrdered delivery. Flush the
        // final value before releasing or the server will correctly reject it afterward.
        Common_SendPorts();
        foreach (uint released in PortsOf(portNetId))
        {
            locallyOwnedPortAuthorities.Remove(released);
            NetworkLifecycle.Instance.Client?.SendTrainControlAuthorityRequest(NetId, released, false);
        }
    }

    public void Client_ReceiveAuthorityUpdate(uint portNetId, ControlAuthorityState state)
    {
        bool shouldBlock = state == ControlAuthorityState.Blocked || state == ControlAuthorityState.Denied;
        bool shouldEnable = !shouldBlock;
        portNetIdToBlockState[portNetId] = shouldBlock;
        if (state != ControlAuthorityState.Released)
            locallyOwnedPortAuthorities.Remove(portNetId);

        Multiplayer.LogDebug(() => $"Client_ReceiveAuthorityUpdate({portNetId}, {state}) for [{CurrentID}, {NetId}]");

        if (!portNetIdToControl.TryGetValue(portNetId, out var control) || control == null)
            return;

        var whistleRopeController = control?.transform.parent.GetComponentInChildren<WhistleRopeController>();
        var handCarBarController = control?.transform.parent.GetComponentInChildren<HandcarBarController>();
        var hinge = control.GetComponent<HingeJoint>();
        Multiplayer.LogDebug(() => $"Client_ReceiveAuthorityUpdate() Control [{control.name}, {portNetId}] for car [{CurrentID}, {NetId}], shouldBlock: {shouldBlock}, isHinge: {hinge != null}, isWhistle: {whistleRopeController != null}");

        if (shouldBlock)
        {
            control.ForceEndInteraction();

            if ((control is IScrollable scrollable) && scrollingControls.TryGetValue(scrollable, out var isScrolling) && isScrolling != null)
            {
                StopCoroutine(isScrolling);
                scrollingControls.Remove(scrollable);
            }
        }

        control.BlockControl(shouldBlock);
        control.InteractionAllowed = shouldEnable;

        if (hinge != null)
            control.enabled = shouldEnable;

        if (whistleRopeController != null)
            whistleRopeController.enabled = shouldEnable;

        if (handCarBarController != null)
            handCarBarController.enabled = shouldEnable;
    }
    public void Client_ResetRelocationBaseline(float speed, uint tick)
    {
        Client_trainSpeedQueue?.Clear();
        Client_trainRigidbodyQueue?.Clear();
        client_bogie1Queue?.Clear();
        client_bogie2Queue?.Clear();
        Client_trainSpeedQueue?.ReceiveSnapshot(speed, tick);
        TrainCar?.stress?.ResetTrainStress();
        kinematicCycles = 0;
    }
    #endregion
}
