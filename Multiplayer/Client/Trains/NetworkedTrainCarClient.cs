using DV;
using DV.CabControls;
using DV.Customization.Paint;
using DV.HUD;
using DV.Simulation.Brake;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using DV.Simulation.Ports;
using LocoSim.Definitions;
using LocoSim.Implementations;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Client.Trains;

/// <summary>
/// The local player's half of a car (<see cref="ITrainCarClientPart"/>), on every game someone
/// plays at, the host's too: its cab and external controls hooked for leases, the player's control
/// changes and chain interactions sent, and on a client, following the host (the host's values
/// played out or predicted, its motion, brakes and commits). Made by the car from the local session
/// (<see cref="NetworkClient.CreateCarPart"/>); client code reaches it as <c>car.Client()</c>.
/// </summary>
public sealed class NetworkedTrainCarClient : ITrainCarClientPart
{
    private readonly NetworkedTrainCar car;

    // The car's own state, as this code read it when it was part of the car.
    private TrainCar TrainCar => car.TrainCar;
    private ushort NetId => car.NetId;
    private string CurrentID => car.CurrentID;
    private Transform transform => car.transform;
    private bool hasSimFlow => car.hasSimFlow;
    private SimulationFlow simulationFlow => car.simulationFlow;
    private ControlPortWriters<uint> portWriters => car.portWriters;
    private BrakeSystem brakeSystem => car.brakeSystem;
    private Bogie bogie1 => car.bogie1;
    private Bogie bogie2 => car.bogie2;
    private HashSet<uint> handcarHandlePorts => car.handcarHandlePorts;
    private HandcarDrive handcarDrive => car.handcarDrive;
    private uint handcarBarPortNetId => car.handcarBarPortNetId;
    private uint handcarEngagedPortNetId => car.handcarEngagedPortNetId;
    private float CarLengthSq => car.CarLengthSq;
    private uint ControlRevision { get => car.ControlRevision; set => car.ControlRevision = value; }
    private uint lastTickProcessed { get => car.lastTickProcessed; set => car.lastTickProcessed = value; }
    private bool Client_Initialized { get => car.Client_Initialized; set => car.Client_Initialized = value; }
    private Coupler couplerInteraction { get => car.couplerInteraction; set => car.couplerInteraction = value; }
    private ChainCouplerInteraction.State originalState { get => car.originalState; set => car.originalState = value; }
    private Coupler originalCoupledTo { get => car.originalCoupledTo; set => car.originalCoupledTo = value; }
    private Coroutine StartCoroutine(IEnumerator routine) => car.StartCoroutine(routine);
    private void StopCoroutine(Coroutine routine) => car.StopCoroutine(routine);
    private static uint GetPortNetId(string portId) => NetworkedTrainCar.GetPortNetId(portId);
    private static string GetPort(uint netId) => NetworkedTrainCar.GetPort(netId);

    public NetworkedTrainCarClient(NetworkedTrainCar car)
    {
        this.car = car;
        if (!NetworkLifecycle.Instance.IsHost())
        {
            trainSpeedQueue = TrainCar.GetOrAddComponent<TrainSpeedQueue>();
            trainRigidbodyQueue = TrainCar.GetOrAddComponent<NetworkedRigidbody>();
            StartCoroutine(InitLater());
        }
    }

    /// <summary>The local player's half of a car; null when this game has no local session for trains.</summary>
    public static NetworkedTrainCarClient Of(NetworkedTrainCar car) => car?.ClientPart as NetworkedTrainCarClient;

    public void Start()
    {
        if (TrainCar.couplers != null)
            foreach (Coupler coupler in TrainCar.couplers)
            {
                //Locos with tenders and tenders only have one chainscript each, no trainscript is used for the hitch between the loco and tender
                if (coupler.ChainScript != null)
                    coupler.ChainScript.StateChanged += (state) => { CouplerStateChange(state, coupler); };
            }

        if (hasSimFlow)
        {
            if (simulationFlow != null && !NetworkLifecycle.Instance.IsHost())
            {
                portPresenter = TrainPortPresenter.Create(car, simulationFlow, portId => portWriters.Classify(GetPortNetId(portId)),
                    port => HoldsPort(GetPortNetId(port.id)));
                foreach (KeyValuePair<string, Port> kvp in simulationFlow.fullPortIdToPort)
                {
                    uint portNetId = GetPortNetId(kvp.Key);
                    if (kvp.Value.valueType == PortValueType.CONTROL && !car.locallyFedPorts.Contains(portNetId))
                        hostPortValues[portNetId] = kvp.Value.Value;
                }
            }

            TrainCar.InteriorLoaded += OnTrainCarInteriorLoaded;
            TrainCar.InteriorAboutToBeUnloaded += OnTrainCarInteriorUnloaded;

            if (TrainCar.loadedInterior != null)
                OnTrainCarInteriorLoaded(TrainCar.loadedInterior.gameObject);

            TrainCar.ExternalInteractableLoaded += OnExternalInteractablesLoaded;
            TrainCar.ExternalInteractableAboutToBeUnloaded += OnExternalInteractablesUnloaded;
            if (TrainCar.loadedExternalInteractables != null)
                OnExternalInteractablesLoaded(TrainCar.loadedExternalInteractables);

            // Ports pulsed on an event (adding coal, igniting firebox, etc)
            // A player adding coal: FireboxTransferCoalPatch; a player shovelling from the coal pile:
            // CoalPileTransferCoalPatch (the coal ports are a stoker's too).
            if (car.firebox != null)
                car.firebox.fireboxIgnitionPort.ValueUpdatedInternally += OnIgnite;      //Player igniting firebox
        }

        // Only a client asks: the host's own reply carried a snapshot from before the spawn's
        // auto-couple, and applying it uncoupled every freshly spawned consist on the host.
        if (!NetworkLifecycle.Instance.IsHost())
            NetworkLifecycle.Instance?.Client?.SendTrainSyncRequest(NetId);
    }

    public void Disable()
    {
        TrainCar.ExternalInteractableLoaded -= OnExternalInteractablesLoaded;
        TrainCar.ExternalInteractableAboutToBeUnloaded -= OnExternalInteractablesUnloaded;
        portPresenter?.Destroy();
        portPresenter = null;

        if (car.firebox != null)
            car.firebox.fireboxIgnitionPort.ValueUpdatedInternally -= OnIgnite;      //Player igniting firebox
    }

    public bool IsBlocked(uint portNetId) => portNetIdToBlockState.TryGetValue(portNetId, out bool isBlocked) && isBlocked;

    public bool WritingHostValues => TrainPortPresenter.Writing;

    public void NoteLocalControlChange()
    {
        // The local player moved one of the train's controls: predict from this frame on, not
        // from when the next tick sends it (TrainPortPresenter).
        if (LocalPlayerCanTouch())
            lastLocalControlAt = Time.realtimeSinceStartup;
    }

    public uint[] PortsToSend(HashSet<uint> dirtyPorts, out List<uint> settling)
    {
        settling = null;
        List<uint> send = new(dirtyPorts.Count);
        foreach (uint id in dirtyPorts)
        {
            if (handcarHandlePorts.Contains(id))
            {
                if (SendsHandcarHandle(id))
                    send.Add(id);
                continue;
            }
            UnleasedChange change = locallyOwnedPortAuthorities.Contains(id) ? UnleasedChange.Send : GetUnleasedChange(id);
            if (change == UnleasedChange.Send)
                send.Add(id);
            else if (change == UnleasedChange.Settling)
                (settling ??= new()).Add(id);
        }
        // Taking hold of the handle: the host needs where it is, which may not have moved.
        if (handcarDrive != null && send.Contains(handcarEngagedPortNetId) && !send.Contains(handcarBarPortNetId))
            send.Add(handcarBarPortNetId);
        return send.ToArray();
    }

    public void SendPorts(uint[] portIds, float[] portValues)
    {
        lastLocalControlAt = Time.realtimeSinceStartup;
        for (int i = 0; i < portIds.Length; i++)
            if (simulationFlow.TryGetPort(GetPort(portIds[i]), out Port _))
                hostPortValues[portIds[i]] = portValues[i];
        ulong operationId = NetworkLifecycle.Instance.Client.SendPorts(NetId, portIds, portValues);
        foreach (uint portNetId in portIds)
            pendingPortOperations[portNetId] = operationId;
    }

    public void SendFuses(uint[] fuseIds, bool[] fuseValues)
    {
        lastLocalControlAt = Time.realtimeSinceStartup;

        ulong operationId = NetworkLifecycle.Instance.Client.SendFuses(NetId, fuseIds, fuseValues);
        foreach (uint fuseNetId in fuseIds)
            pendingFuseOperations[fuseNetId] = operationId;
    }

    public void SendHandbrake(bool viaRemote) =>
        pendingHandbrakeOperation = NetworkLifecycle.Instance.Client.SendHandbrakePositionChanged(NetId, brakeSystem.handbrakePosition, viaRemote);

    public void SendBrakeCylinderReleased() => NetworkLifecycle.Instance.Client.SendBrakeCylinderReleased(NetId);

    public void SendPaintTheme(TrainCarPaint.Target target, uint themeNetId) =>
        NetworkLifecycle.Instance.Client.SendPaintThemeChange(car, target, themeNetId);

    public bool TakeHostPortValue(uint portNetId, Port port, float value, uint hostTick)
    {
        hostPortValues[portNetId] = value;
        hostPortReceivedAt[portNetId] = Time.realtimeSinceStartup;
        return portPresenter != null && portPresenter.TakeHostValue(port, value, hostTick);
    }

    // How far an unleased control's port must be from the host's last value to count as the
    // player's change rather than simulation feedback (GetUnleasedChange).
    private const float UNLEASED_CHANGE_TOLERANCE = 0.02f;
    // And how long after a host update to a port its control may still be catching up to it.
    private const float UNLEASED_CHANGE_SETTLE_SECONDS = 1.5f;
    private const uint MIN_KINEMATIC_CYCLES = 10;
    // Periodic full-sync positions are only a safety net: TrainSpeedQueue steers on-rail cars to
    // the host's bogie positions continuously, and snapping a moving car to a latency-old pose
    // yanks it backwards. Only snap when the car is clearly somewhere else.
    private const float POSITION_UPDATE_THRESHOLD = 5f;
    private const float INTERIOR_CONTROLS_TIMEOUT = 2f;
    // The speed a client wakes a sleeping car for: the host counts it as moving.
    private const float MOVING_SPEED = 0.01f;

    private InteriorControlsManager interiorControlsManager;
    private readonly Dictionary<ControlImplBase, Action<ValueChangedEventArgs>> scrollableControlDelegates = [];
    private readonly Dictionary<IScrollable, Coroutine> scrollingControls = [];
    private readonly Dictionary<ControlImplBase, Action> usedControlDelegates = [];
    // A control's first port -> the other ports it feeds.
    private readonly Dictionary<uint, uint[]> linkedPortNetIds = [];
    // Controls pressed without a grab (VR buttons and switches) whose lease is handed back shortly.
    private readonly Dictionary<ControlImplBase, Coroutine> usedControlReleases = [];
    // Vanilla controllers that write a hooked control's port every frame from this game's own
    // input (LocalInputWriters), by the ports they write: off while another player holds the lease.
    private readonly Dictionary<uint, Behaviour[]> portNetIdToLocalInputWriters = [];
    private const float USED_CONTROL_LEASE_SECONDS = 0.5f;

    // Client: controls this client changed in operations the host hasn't committed back yet.
    // Accepted commits are broadcast to everyone, including the sender, a round trip later; a
    // commit for an older operation must not drag a lever back from where the player has since
    // moved it (or leave it there once they stop).
    private readonly Dictionary<uint, ulong> pendingPortOperations = new();
    private readonly Dictionary<uint, ulong> pendingFuseOperations = new();
    private ulong pendingHandbrakeOperation;

    public TickedQueue<float> trainSpeedQueue;
    public TickedQueue<RigidbodySnapshot> trainRigidbodyQueue;
    public NetworkedBogie bogie1Queue;
    public NetworkedBogie bogie2Queue;

    private uint kinematicCycles = 0;

    private readonly Dictionary<uint, bool> portNetIdToBlockState = [];
    private readonly Dictionary<uint, ControlImplBase> portNetIdToControl = [];
    private readonly Dictionary<ControlImplBase, uint> controlToPortNetId = [];
    // The server deliberately does not echo a successful lease grant to its requester.
    // Track the leases we requested locally so client simulation feedback cannot be
    // bundled with a control the client does not own.
    private readonly HashSet<uint> locallyOwnedPortAuthorities = [];
    // Each control port's value as the host last had it, to tell a player's change to a control
    // that took no lease from the host's own values echoing back (GetUnleasedChange).
    private readonly Dictionary<uint, float> hostPortValues = [];
    private readonly Dictionary<uint, float> hostPortReceivedAt = [];
    // How a client shows the simulation's outputs: played out smoothly, or predicted while the
    // local player drives (TrainPortPresenter).
    private TrainPortPresenter portPresenter;
    private float lastLocalControlAt = float.NegativeInfinity;
    private static readonly Dictionary<Trainset, (int frame, bool driving)> drivingByTrainset = [];
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
                void ScrollChanged(ValueChangedEventArgs args) => ControlScrolled(control);
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
        RequestInteriorResync();
    }

    private const float INTERIOR_RESYNC_INTERVAL = 10f;
    private float nextInteriorResyncAt;
    private bool interiorResyncPending;

    // A player standing at the distance an interior loads at reloads it every few seconds, and
    // each full resync makes the host send the whole car to everyone (playtest 2: one car every
    // 5-15 s, two requests a time). One per car per interval; any more in it make one at its end.
    private void RequestInteriorResync()
    {
        if (Time.realtimeSinceStartup >= nextInteriorResyncAt)
        {
            nextInteriorResyncAt = Time.realtimeSinceStartup + INTERIOR_RESYNC_INTERVAL;
            NetworkLifecycle.Instance?.Client.SendTrainSyncRequest(NetId);
        }
        else if (!interiorResyncPending)
        {
            interiorResyncPending = true;
            CoroutineManager.Instance.StartCoroutine(InteriorResyncLater());
        }
    }

    private IEnumerator InteriorResyncLater()
    {
        yield return new WaitForSecondsRealtime(nextInteriorResyncAt - Time.realtimeSinceStartup);
        interiorResyncPending = false;
        if (car != null)
            RequestInteriorResync();
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
        // Some controls are on the external interactables and in the cab's controls manager both
        // (a handcar's handle): hook once.
        if (controlToPortNetId.ContainsKey(implementation))
            return;

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
            ObiGrabHandler.Grabbed += ControlGrabbed;
            ObiGrabHandler.Ungrabbed += ControlUngrabbed;

            portNetIdToControl[netId] = ObiGrabHandler;
            controlToPortNetId[ObiGrabHandler] = netId;
        }
        else
        {
            // Standard control grab/ungrab events
            implementation.Grabbed += ControlGrabbed;
            implementation.Ungrabbed += ControlUngrabbed;

            // Buttons and switches in VR, and keyboard presses, work through Use() and never
            // raise Grabbed.
            void Used() => ControlUsed(implementation);
            usedControlDelegates[implementation] = Used;
            implementation.Used += Used;

            portNetIdToControl[netId] = implementation;
            controlToPortNetId[implementation] = netId;

            uint[] linked = implementation.GetComponents<InteractablePortFeeder>()
                .Select(f => f.portId)
                .Where(portId => !string.IsNullOrEmpty(portId))
                .Select(GetPortNetId)
                .Where(id => id != netId && !portNetIdToControl.ContainsKey(id))
                .Distinct().ToArray();
            if (linked.Length > 0)
            {
                linkedPortNetIds[netId] = linked;
                foreach (uint id in linked)
                    portNetIdToControl[id] = implementation;
            }
        }

        Behaviour[] writers = LocalInputWriters(implementation);
        if (writers.Length > 0)
            foreach (uint id in PortsOf(netId))
                portNetIdToLocalInputWriters[id] = writers;

        if (portNetIdToBlockState.TryGetValue(netId, out var isBlocked) && isBlocked)
        {
            Multiplayer.LogDebug(() => $"HookControl() Control [{implementation.name}, {netId}] is blocked on car {CurrentID}");
            ReceiveAuthorityUpdate(netId, ControlAuthorityState.Blocked);
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
                ReleasePortAuthority(usedPortNetId);
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
                ControlUngrabbed(control);

            control.Grabbed -= ControlGrabbed;
            control.Ungrabbed -= ControlUngrabbed;
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
        portNetIdToLocalInputWriters.Clear();

        interiorControlsManager = null;

        // The external interactables outlive the cab.
        if (TrainCar.loadedExternalInteractables != null)
            HookExternalControls(TrainCar.loadedExternalInteractables);
    }

    // Controls on the car body rather than in the cab: doors, windows and hatches, and on steam
    // locos the compressor, dynamo, lubricator and oiling points, a DE6's starter and primer, a
    // handcar's handle. They're on the external interactables, which load near the car, while the
    // cab loads only once a player is on it (and never on a dedicated server). Unhooked, a grab
    // took no lease: the change went only as an unleased one, after the settle window, and nothing
    // kept another game's copy from writing the port too (the host's own HandcarBarController
    // wrote the handle as let go every frame, so a handcar never moved). Loading an exploded loco
    // swaps in its exploded external interactables, through the same event.
    private void OnExternalInteractablesLoaded(GameObject externalInteractables)
    {
        // Raised with null once they're unloaded.
        if (externalInteractables != null)
            StartCoroutine(HookExternalControlsWhenReady(externalInteractables));
    }

    private IEnumerator HookExternalControlsWhenReady(GameObject externalInteractables)
    {
        // Controls are built from their specs over the first frames.
        float timeOut = Time.time;
        yield return new WaitUntil(() =>
            externalInteractables == null ||
            externalInteractables.GetComponentsInChildren<InteractablePortFeeder>(true).All(f => f.GetComponent<ControlImplBase>() != null) ||
            Time.time - timeOut > INTERIOR_CONTROLS_TIMEOUT);
        if (externalInteractables != null && externalInteractables == TrainCar.loadedExternalInteractables)
            HookExternalControls(externalInteractables);
    }

    private void HookExternalControls(GameObject externalInteractables)
    {
        if (!hasSimFlow)
            return;

        foreach (InteractablePortFeeder feeder in externalInteractables.GetComponentsInChildren<InteractablePortFeeder>(true))
        {
            ControlImplBase implementation = feeder.GetComponent<ControlImplBase>();
            if (implementation == null || string.IsNullOrEmpty(feeder.portId) || controlToPortNetId.ContainsKey(implementation))
                continue;
            // A handbrake's feeder: handbrakes are synced on their own, without a lease.
            if (feeder.GetComponent<HandbrakeFeeder>() != null)
                continue;

            Multiplayer.LogDebug(() => $"HookExternalControls() Control [{feeder.portId}] found on car {CurrentID}");
            HookControl(implementation, GetPortNetId(feeder.portId));
        }
    }

    private void OnExternalInteractablesUnloaded(GameObject externalInteractables)
    {
        if (externalInteractables == null)
            return;

        foreach (ControlImplBase control in controlToPortNetId.Keys.ToList())
            if (control != null && control.transform.IsChildOf(externalInteractables.transform))
                UnhookControl(control);
    }

    private void UnhookControl(ControlImplBase control)
    {
        if (!controlToPortNetId.TryGetValue(control, out uint portNetId))
            return;

        if (usedControlReleases.TryGetValue(control, out var pending))
        {
            if (pending != null)
                StopCoroutine(pending);
            usedControlReleases.Remove(control);
            ReleasePortAuthority(portNetId);
        }
        else if (control.IsGrabbed())
        {
            ControlUngrabbed(control);
        }

        control.Grabbed -= ControlGrabbed;
        control.Ungrabbed -= ControlUngrabbed;
        if (usedControlDelegates.TryGetValue(control, out var used))
        {
            control.Used -= used;
            usedControlDelegates.Remove(control);
        }

        foreach (uint id in PortsOf(portNetId).ToList())
        {
            if (portNetIdToControl.TryGetValue(id, out ControlImplBase mapped) && mapped == control)
                portNetIdToControl.Remove(id);
            portNetIdToLocalInputWriters.Remove(id);
        }
        linkedPortNetIds.Remove(portNetId);
        controlToPortNetId.Remove(control);
    }

    /// <summary>
    /// Vanilla controllers that write a control's port every frame from this game's own input,
    /// whoever is using the control: the whistle rope's tension, through its dummy lever. Every
    /// game runs them, so while a player on another game uses the control, this game's would write
    /// it back as let go (the host's would win). They're turned off while another player holds the
    /// control's lease (<see cref="ReceiveAuthorityUpdate"/>). Other controls write their
    /// port only on this game's player's input, which a lease already covers. A handcar's
    /// HandcarBarController writes whether its handle is held the same way, but that port is each
    /// game's own player's (<see cref="handcarHandlePorts"/>), so it's left on.
    /// </summary>
    private static Behaviour[] LocalInputWriters(ControlImplBase control)
    {
        List<Behaviour> writers = [];
        Transform parent = control.transform.parent;
        // The whistle's control is its rope's dummy lever, hooked with the rope's grab handler
        // beside it (HookControl).
        bool ropeControl = parent != null && parent.GetComponentInChildren<ObiRopeGrabAreaHandler>(false) != null;
        if (parent != null)
            foreach (WhistleRopeController rope in parent.GetComponentsInChildren<WhistleRopeController>(true))
                if (rope.dummyLeverGO == control.gameObject || ropeControl)
                    writers.Add(rope);
        return writers.ToArray();
    }

    public void ReceiveControlState(TrainControlStateData state)
    {
        if (state == null || state.Revision < ControlRevision)
            return;
        ReceiveControlCommit(new ClientboundTrainControlCommitPacket
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

    public void SetControlRevision(uint revision)
    {
        if (revision > ControlRevision)
            ControlRevision = revision;
    }

    public void ReceiveControlCommit(ClientboundTrainControlCommitPacket packet)
    {
        if (packet == null)
            return;
        var client = NetworkLifecycle.Instance.Client;
        if (client != null && client.IsOwnTrainControlOperation(packet.OperationId))
        {
            AcknowledgeControlOperation(packet.OperationId);
            // The host didn't take our change; get its state back rather than keep ours.
            if (packet.Result != TrainControlCommitResult.Accepted && !NetworkLifecycle.Instance.IsHost())
                client.SendTrainSyncRequest(NetId);
        }
        // Another player's handbrake operation (a join's state carries no operation).
        else if (packet.IncludesHandbrake && packet.OperationId != 0 &&
                 packet.Result == TrainControlCommitResult.Accepted)
            car.Common_OnRemoteHandbrakeChange();
        // Every commit is stamped with the host's tick: their arrivals measure the link's jitter.
        if (packet.Tick != 0 && !NetworkLifecycle.Instance.IsHost())
            TrainPortPresenter.Clock.OnArrival(packet.Tick, TrainPortPresenter.Now);
        if (packet.Revision < ControlRevision)
            return;

        // Skip controls we have newer changes in flight for: those will commit after this.
        if (packet.PortIds?.Length > 0 && packet.PortValues?.Length == packet.PortIds.Length)
        {
            var (portIds, portValues) = WithoutPending(packet.PortIds, packet.PortValues, pendingPortOperations);
            if (portIds.Length > 0)
                car.Common_UpdatePorts(new CommonTrainPortsPacket
                {
                    NetId = NetId,
                    PortIds = portIds,
                    PortValues = portValues
                }, packet.Tick);
        }
        if (packet.FuseIds?.Length > 0 && packet.FuseValues?.Length == packet.FuseIds.Length)
        {
            var (fuseIds, fuseValues) = WithoutPending(packet.FuseIds, packet.FuseValues, pendingFuseOperations);
            if (fuseIds.Length > 0)
                car.Common_UpdateFuses(new CommonTrainFusesPacket
                {
                    NetId = NetId,
                    FuseIds = fuseIds,
                    FuseValues = fuseValues
                });
        }
        if (packet.IncludesHandbrake && brakeSystem?.hasHandbrake == true && pendingHandbrakeOperation == 0 &&
            Math.Abs(brakeSystem.handbrakePosition - packet.HandbrakePosition) > 0.0001f)
            brakeSystem.SetHandbrakePosition(packet.HandbrakePosition);
        if (packet.BrakeCylinderReleased)
            brakeSystem?.ReleaseBrakeCylinderPressure();
        ControlRevision = packet.Revision;
    }

    // Our operation ids increase monotonically, so a commit for one settles everything we sent
    // up to and including it.
    private void AcknowledgeControlOperation(ulong operationId)
    {
        if (pendingHandbrakeOperation != 0 && pendingHandbrakeOperation <= operationId)
            pendingHandbrakeOperation = 0;
        RemoveSettled(pendingPortOperations, operationId);
        RemoveSettled(pendingFuseOperations, operationId);

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

    /// <summary>
    /// Whether the local player is driving this car's train: holding one of its controls, or
    /// having changed one within <paramref name="holdSeconds"/>, on any car of the train (a
    /// loco's controls drive its tender and multiple-unit followers too). A client shows a
    /// driven train's outputs undelayed and predicts the ones its controls drive
    /// (<see cref="TrainPortPresenter"/>).
    /// </summary>
    internal bool IsLocalPlayerDrivingTrain(float holdSeconds)
    {
        Trainset trainset = TrainCar.trainset;
        if (trainset == null)
            return IsLocalPlayerDriving(holdSeconds);
        int frame = Time.frameCount;
        if (drivingByTrainset.TryGetValue(trainset, out var cached) && cached.frame == frame)
            return cached.driving;

        bool driving = false;
        foreach (TrainCar other in trainset.cars)
            if (other != null && NetworkedTrainCar.TryGetFromTrainCar(other, out NetworkedTrainCar networkedCar) &&
                Of(networkedCar)?.IsLocalPlayerDriving(holdSeconds) == true)
            {
                driving = true;
                break;
            }
        // Trainsets are replaced as cars couple and uncouple: don't keep old ones.
        if (drivingByTrainset.Count > 256)
            drivingByTrainset.Clear();
        drivingByTrainset[trainset] = (frame, driving);
        return driving;
    }

    // The local player has this control: they hold its lease, or a change of theirs to it hasn't
    // been committed yet. The host's values for it aren't played out over their hand.
    private bool HoldsPort(uint portNetId) =>
        locallyOwnedPortAuthorities.Contains(portNetId) || pendingPortOperations.ContainsKey(portNetId);

    private bool IsLocalPlayerDriving(float holdSeconds) =>
        locallyOwnedPortAuthorities.Count > 0 || Time.realtimeSinceStartup - lastLocalControlAt < holdSeconds;

    /// <summary>
    /// Whether the local player is close enough to have changed this car's controls by hand: the
    /// same distance the host checks. A client's own simulation also sets handbrakes and fuses
    /// (notably while the world loads); reporting those for distant cars only produced rejected
    /// operations and resync requests. The host always reports.
    /// </summary>
    public bool LocalPlayerCanTouch() =>
        NetworkLifecycle.Instance.IsHost() ||
        (PlayerManager.PlayerTransform != null &&
         (PlayerManager.PlayerTransform.position - transform.position).sqrMagnitude <= CarLengthSq);

    // A control the player changed without anything taking its lease: a valve, lever or cylinder
    // cock turned in VR (they don't all raise Grabbed), or a control on the car body like a cab
    // window or an oiling point, which isn't hooked at all. The client dropped these as feedback, so
    // only the player saw the change (#73 covered buttons and switches, which raise Used). The host
    // takes a change nobody holds a lease on. Only controls players drive count (ControlPortWriters):
    // DV and mods set some CONTROL ports from the simulation. Feedback is told apart by its value:
    // once settled it's what the host last sent.
    private UnleasedChange GetUnleasedChange(uint portNetId)
    {
        if (!hasSimFlow || portWriters == null || !simulationFlow.TryGetPort(GetPort(portNetId), out Port port))
            return UnleasedChange.No;
        bool blocked = portNetIdToBlockState.TryGetValue(portNetId, out bool isBlocked) && isBlocked;
        float? hostValue = hostPortValues.TryGetValue(portNetId, out float value) ? value : null;
        float? sinceHostValue = hostPortReceivedAt.TryGetValue(portNetId, out float receivedAt)
            ? Time.realtimeSinceStartup - receivedAt : null;
        return portWriters.Unleased(portNetId, blocked, LocalPlayerCanTouch(), port.Value, hostValue, sinceHostValue,
            UNLEASED_CHANGE_TOLERANCE, UNLEASED_CHANGE_SETTLE_SECONDS);
    }

    // This game's player's handcar handle: whether they hold it, whenever that changes (vanilla's
    // HandcarBarController writes it every frame), and where it is while they hold it. Let go, the
    // handle follows the crank, which the host doesn't need.
    private bool SendsHandcarHandle(uint portNetId)
    {
        if (portNetId == handcarEngagedPortNetId)
            return true;
        return handcarDrive != null && handcarDrive.handleEngagedExtIn.Value > 0f;
    }

    private IEnumerator InitLater()
    {
        while ((bogie1Queue = bogie1.GetComponent<NetworkedBogie>()) == null)
            yield return null;
        while ((bogie2Queue = bogie2.GetComponent<NetworkedBogie>()) == null)
            yield return null;

        Client_Initialized = true;
    }

    private float nextDerailResyncRequestAt;

    // Put a car the host has on the rails back on the host's track where the host has it. That
    // needs a full sync (pose and track); ask for one until it comes. Rerailing uncouples the car
    // locally (we're applying a host packet, so that isn't reported), so restore the coupling from
    // the host's topology once the rerail has finished.
    private void RerailToHost(in TrainsetMovementPart movementPart)
    {
        if (TrainCar.IsTeleporting)
            return;
        BogieData bogie = movementPart.Bogie1.IncludesTrackData ? movementPart.Bogie1 : movementPart.Bogie2;
        if (!movementPart.typeFlag.HasFlag(TrainsetMovementPart.MovementType.Position) || !bogie.IncludesTrackData ||
            !global::Multiplayer.Shared.World.NetworkedRailTrack.TryGet(bogie.TrackNetId, out global::Multiplayer.Shared.World.NetworkedRailTrack track) || track.RailTrack == null)
        {
            if (Time.realtimeSinceStartup >= nextDerailResyncRequestAt)
            {
                nextDerailResyncRequestAt = Time.realtimeSinceStartup + 2f;
                NetworkLifecycle.Instance.Client?.SendTrainSyncRequest(NetId);
            }
            return;
        }

        Multiplayer.LogWarning($"{CurrentID} derailed here but not on the host; rerailing it to the host's track");
        BeginRelocation();
        TrainCar.Rerail(track.RailTrack, movementPart.Position + WorldMover.currentMove,
            movementPart.Rotation * Vector3.forward);
        FinishRelocationLater();
        CoroutineManager.Instance.StartCoroutine(RestoreTopologyAfterRerail());
    }

    /// <summary>The host rerailed this car (<see cref="TrainCar.Rerail"/>).</summary>
    public void RerailFromHost(RailTrack track, Vector3 worldPos, Vector3 forward)
    {
        if (!TrainCar.derailed)
            return;
        BeginRelocation();
        TrainCar.Rerail(track, worldPos, forward);
        FinishRelocationLater();
    }

    /// <summary>The host moved this car to another track (<see cref="TrainCar.MoveToTrackWithCarUncouple"/>).</summary>
    public void MoveToTrackFromHost(RailTrack track, Vector3 worldPos, Vector3 forward)
    {
        if (TrainCar.derailed)
            return;
        BeginRelocation();
        TrainCar.MoveToTrackWithCarUncouple(track, worldPos, forward);
        FinishRelocationLater();
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
    private void BeginRelocation()
    {
        ResetFollowers();
        if (TrainCar.rb != null && TrainCar.rb.isKinematic)
            TrainCar.rb.isKinematic = false;
    }

    private void ResetFollowers()
    {
        trainSpeedQueue?.Clear();
        trainRigidbodyQueue?.Clear();
        bogie1Queue?.Clear();
        bogie2Queue?.Clear();
        kinematicCycles = 0;
        TrainCar?.stress?.ResetTrainStress();
    }

    /// <summary>
    /// Vanilla relocates the car a physics step later (a coroutine); samples arriving meanwhile
    /// are ignored (see <see cref="ReceiveTrainPhysicsUpdate"/>), so once it's done, reset
    /// again and ask the host for the car's pose and bogie tracks.
    /// </summary>
    private void FinishRelocationLater() => CoroutineManager.Instance.StartCoroutine(FinishRelocation());

    private IEnumerator FinishRelocation()
    {
        for (float t0 = Time.realtimeSinceStartup; TrainCar != null && TrainCar.IsTeleporting && Time.realtimeSinceStartup - t0 < 5f;)
            yield return null;
        yield return new WaitForFixedUpdate();
        if (car == null || TrainCar == null)
            yield break;
        ResetFollowers();
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
    public bool ReceiveTrainPhysicsUpdate(in TrainsetMovementPart movementPart, uint tick, int hostSetSize = 1)
    {
        using IDisposable debugScope = DebugTrace.BeginApply("train", "train.physics-apply", DebugRuntimeSide.Client, "TrainCar", NetId.ToString(), () =>
        {
            EntityDebugRegistry.RegisterTrain(car);
            return EntityDebugRegistry.Get("TrainCar", NetId.ToString())?.LatestState;
        }, highFrequency: true);
        if (DebugRuntime.EnabledFor("train") && DebugRuntime.ShouldEmitHighFrequency("TrainCar", NetId.ToString()))
            DebugRuntime.Publish("train", "train.physics-received", DebugRuntimeSide.Client, entityType: "TrainCar", entityId: NetId.ToString(),
                data: DebugValueSnapshotter.SnapshotObject(movementPart), highFrequency: true, samplingDecided: true);
        if (!Client_Initialized)
            return false;

        // Mid rerail or move (vanilla takes a physics step): the sample is for the car's new spot,
        // ours isn't there yet. FinishRelocation asks for a fresh one.
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
                trainSpeedQueue.Clear();
                bogie1Queue.Clear();
                bogie2Queue.Clear();
                if (TrainCar.rb.isKinematic)
                    TrainCar.rb.isKinematic = false;
                kinematicCycles = 0;
                using (TrainChangeCauses.Scope(TrainChangeCauses.Cause(DerailReason.FollowingHost)))
                    TrainCar.Derail();
            }
            trainRigidbodyQueue.ReceiveSnapshot(movementPart.RigidbodySnapshot, tick);

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
                RerailToHost(movementPart);
                return false;
            }

            // Only force awake if there's movement
            if (Mathf.Abs(movementPart.Speed) > MOVING_SPEED && TrainCar.isEligibleForSleep)
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
                    trainSpeedQueue.Clear();
                    trainRigidbodyQueue.Clear();
                    bogie1Queue.Clear();
                    bogie2Queue.Clear();

                    TrainCar.stress.ResetTrainStress();
                }
            }

            if (trainSpeedQueue is TrainSpeedQueue speedQueue)
                speedQueue.HostCoupled = hostSetSize > 1;
            trainSpeedQueue.ReceiveSnapshot(movementPart.Speed, tick);
            TrainCar.stress.slowBuildUpStress = movementPart.SlowBuildUpStress;
            bogie1Queue.ReceiveSnapshot(movementPart.Bogie1, movementPart.Speed, tick);
            bogie2Queue.ReceiveSnapshot(movementPart.Bogie2, movementPart.Speed, tick);
        }

        // Only write isKinematic when it changes: the write itself disturbs the body. On-rail cars
        // are dynamic except while TrainSpeedQueue slides one into place.
        if (movementPart.typeFlag == TrainsetMovementPart.MovementType.RigidBody)
            UpdateDerailedKinematic(movementPart.RigidbodySnapshot);
        else if (TrainCar.rb.isKinematic && !(trainSpeedQueue is TrainSpeedQueue { Kinematic: true }))
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

    public bool ReceiveBrakeStateUpdate(ClientboundBrakeStateUpdatePacket packet)
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

    private void OnIgnite(float ignition)
    {
        if (NetworkLifecycle.Instance.IsProcessingPacket || TrainPortPresenter.Writing)
            return;

        // The host's fire is lit in the world it already owns.
        if (ignition == 0f || NetworkLifecycle.Instance.IsHost())
            return;

        NetworkLifecycle.Instance.Client.LogDebug(() => $"Common_OnIgnite({CurrentID})");
        NetworkLifecycle.Instance.Client.SendFireboxIgnition(NetId);
    }

    public void CouplerStateChange(ChainCouplerInteraction.State state, Coupler coupler)
    {
        //Multiplayer.LogDebug(() => $"1 CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}], coupler is front: {coupler?.isFrontCoupler}");

        //if we are processing a packet, then these state changes are likely triggered by a received update, not player interaction
        //in future, maybe patch OnGrab() or add logic to add/remove action subscriptions
        if (NetworkLifecycle.Instance.IsProcessingPacket || car.IsAuthoritativeTopologyObservationSuppressed)
            return;

        CouplerInteractionType interactionFlags = CouplerInteractionType.NoAction;
        Coupler otherCoupler = null;

        switch (state)
        {
            case ChainCouplerInteraction.State.Being_Dragged:
                couplerInteraction = coupler;
                NetworkedTrainCar.localChainDrags.Add(coupler);
                originalState = coupler.state;
                originalCoupledTo = coupler.coupledTo;
                // Dragging is presentation local to the manipulating player. Sending the
                // native FSM's transient states made the host and topology reconciler race
                // one another. Only the completed placement is committed below.
                //Multiplayer.LogDebug(() => $"3 CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}]");
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
                    //Multiplayer.LogDebug(() => $"4 CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}] coupledTo: {coupler?.coupledTo?.train?.ID}, first Coupler: {otherCoupler?.train?.ID}");
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
            //Multiplayer.LogDebug(() => $"8 CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}], coupler is front: {coupler?.isFrontCoupler}, Sending: {interactionFlags}");
            NetworkLifecycle.Instance.Client.SendCouplerInteraction(interactionFlags, coupler, otherCoupler);

            //finished interaction, clear flag
            if (interactionFlags != CouplerInteractionType.Start)
                couplerInteraction = null;

            return;
        }
        //Multiplayer.LogDebug(() => $"9 CouplerStateChange({state}) trainCar: [{TrainCar?.ID}, {NetId}]");
    }

    private void ControlGrabbed(ControlImplBase control)
    {
        Multiplayer.LogDebug(() => $"ControlGrabbed() Control {control.name}, car: {CurrentID}");
        if (!controlToPortNetId.TryGetValue(control, out var portNetId))
        {
            Multiplayer.LogWarning($"Control \"{control.name}\" grabbed but netId not found on TrainCar \"{CurrentID}\", hash: {control.GetHashCode()}, instance: {control.GetInstanceID()}");
            return;
        }

        // A handcar's handle takes no lease: every player pumps their own (handcarHandlePorts).
        if (handcarHandlePorts.Contains(portNetId))
            return;

        if (portNetIdToBlockState.TryGetValue(portNetId, out var isBlocked) && isBlocked)
        {
            Multiplayer.LogDebug(() => $"ControlGrabbed() Control [{control.name}, {portNetId}] is blocked on car {CurrentID}, ending interaction");
            control.ForceEndInteraction();
        }
        else
        {
            Multiplayer.LogDebug(() => $"ControlGrabbed() Control [{control.name}, {portNetId}] is not blocked on car {CurrentID}, requesting authority");
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

    private void ControlScrolled(InteriorControlsManager.ControlReference controlRef)
    {
        if (controlRef.controlImplBase.IsHoverScrolled())
        {
            // Currently scrolling
            Multiplayer.LogDebug(() => $"ControlScrolled() Control {controlRef.overridableBaseControl.ControlType} on car {CurrentID} was scrolled while hovered, requesting authority");

            if (!scrollingControls.ContainsKey(controlRef.scrollable))
            {
                scrollingControls[controlRef.scrollable] = StartCoroutine(CheckScrolling(controlRef));
                ControlGrabbed(controlRef.controlImplBase);
            }
        }
    }

    // A press without a grab: a VR button or toggle switch (they never raise Grabbed), or a keyboard
    // shortcut. Used fires before the control changes its value, so leasing here means the change is
    // sent; the lease is handed back once it has gone out, as for a released grab.
    private void ControlUsed(ControlImplBase control)
    {
        if (control.IsGrabbed() || !controlToPortNetId.TryGetValue(control, out var portNetId) ||
            handcarHandlePorts.Contains(portNetId))
            return;

        if (!locallyOwnedPortAuthorities.Contains(portNetId))
            ControlGrabbed(control);

        if (usedControlReleases.TryGetValue(control, out var pending) && pending != null)
            StopCoroutine(pending);
        usedControlReleases[control] = StartCoroutine(ReleaseUsedControl(control));
    }

    private IEnumerator ReleaseUsedControl(ControlImplBase control)
    {
        // Past the next tick, which sends the change.
        yield return new WaitForSecondsRealtime(USED_CONTROL_LEASE_SECONDS);
        usedControlReleases.Remove(control);
        if (control != null && !control.IsGrabbed())
            ControlUngrabbed(control);
    }

    private IEnumerator CheckScrolling(InteriorControlsManager.ControlReference controlRef)
    {
        // Wait for player to stop scrolling
        while (controlRef.controlImplBase.IsHoverScrolled())
            yield return new WaitForSecondsRealtime(0.5f);

        Multiplayer.LogDebug(() => $"CheckScrolling() Control {controlRef.overridableBaseControl.ControlType} on car {CurrentID} scrolling complete");
        scrollingControls.Remove(controlRef.scrollable);
        ControlUngrabbed(controlRef.controlImplBase);
    }

    private void ControlUngrabbed(ControlImplBase control)
    {
        Multiplayer.LogDebug(() => $"ControlUngrabbed() Control {control.name}, car: {CurrentID}");
        if (!controlToPortNetId.TryGetValue(control, out var portNetId))
        {
            Multiplayer.LogWarning($"Control \"{control.name}\" ungrabbed but netId not found on TrainCar \"{CurrentID}\"");
            return;
        }
        if (handcarHandlePorts.Contains(portNetId))
            return;

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
            Multiplayer.LogDebug(() => $"ControlUngrabbed() Control [{control.name}, {portNetId}] not blocked, releasing authority for car {CurrentID}");
            ReleasePortAuthority(portNetId);
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
            ReleasePortAuthority(portNetId);
        }
    }

    private void ReleasePortAuthority(uint portNetId)
    {
        // Authority requests and port commits share ReliableOrdered delivery. Flush the
        // final value before releasing or the server will correctly reject it afterward.
        car.Common_SendPorts();
        foreach (uint released in PortsOf(portNetId))
        {
            locallyOwnedPortAuthorities.Remove(released);
            NetworkLifecycle.Instance.Client?.SendTrainControlAuthorityRequest(NetId, released, false);
        }
    }

    public void ReceiveAuthorityUpdate(uint portNetId, ControlAuthorityState state)
    {
        bool shouldBlock = state == ControlAuthorityState.Blocked || state == ControlAuthorityState.Denied;
        bool shouldEnable = !shouldBlock;
        portNetIdToBlockState[portNetId] = shouldBlock;
        if (state != ControlAuthorityState.Released)
            locallyOwnedPortAuthorities.Remove(portNetId);

        Multiplayer.LogDebug(() => $"ReceiveAuthorityUpdate({portNetId}, {state}) for [{CurrentID}, {NetId}]");

        if (portNetIdToLocalInputWriters.TryGetValue(portNetId, out Behaviour[] writers))
            foreach (Behaviour writer in writers)
                if (writer != null)
                    writer.enabled = shouldEnable;

        // Several controls can feed one port (an FEF-3's two lubricator buttons): block them all.
        List<ControlImplBase> controls = controlToPortNetId.Where(kvp => kvp.Value == portNetId && kvp.Key != null).Select(kvp => kvp.Key).ToList();
        if (portNetIdToControl.TryGetValue(portNetId, out var mapped) && mapped != null && !controls.Contains(mapped))
            controls.Add(mapped);

        foreach (ControlImplBase control in controls)
        {
            var hinge = control.GetComponent<HingeJoint>();
            Multiplayer.LogDebug(() => $"ReceiveAuthorityUpdate() Control [{control.name}, {portNetId}] for car [{CurrentID}, {NetId}], shouldBlock: {shouldBlock}, isHinge: {hinge != null}, localInputWriters: {writers?.Length ?? 0}");

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
        }
    }
    public void ResetRelocationBaseline(float speed, uint tick)
    {
        trainSpeedQueue?.Clear();
        trainRigidbodyQueue?.Clear();
        bogie1Queue?.Clear();
        bogie2Queue?.Clear();
        trainSpeedQueue?.ReceiveSnapshot(speed, tick);
        TrainCar?.stress?.ResetTrainStress();
        kinematicCycles = 0;
    }
}

public static class NetworkedTrainCarClientExtensions
{
    /// <summary>The local player's half of the car (<see cref="NetworkedTrainCarClient"/>).</summary>
    public static NetworkedTrainCarClient Client(this NetworkedTrainCar car) => NetworkedTrainCarClient.Of(car);
}
