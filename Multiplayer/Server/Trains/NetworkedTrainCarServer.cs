using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV;
using DV.Customization.Paint;
using DV.Damage;
using DV.ThingTypes;
using LocoSim.Implementations;
using Multiplayer.Core.Trains;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using UnityEngine;
using Multiplayer.Client.Session;

namespace Multiplayer.Server.Trains;

/// <summary>
/// The server's half of a car (<see cref="ITrainCarServerPart"/>): who holds its controls and
/// couplers, validating players' changes to it, and sending its state to the players. Made by the
/// car on the server (<see cref="NetworkServer.CreateCarPart"/>); server code reaches it as
/// <c>car.Server()</c>.
/// </summary>
public sealed class NetworkedTrainCarServer : ITrainCarServerPart
{
    private const float MAX_PORT_DELTA = 0.001f;
    private const float DISTANCE_TOLERANCE = 2f;
    private const float MAX_PAINT_DISTANCE_SQ = (CommsRadioPaintjob.SIGNAL_RANGE + DISTANCE_TOLERANCE) * (CommsRadioPaintjob.SIGNAL_RANGE + DISTANCE_TOLERANCE);

    private readonly NetworkedTrainCar car;
    private TrainCar TrainCar => car.TrainCar;
    private ushort NetId => car.NetId;
    private string CurrentID => car.CurrentID;

    //Coupler interaction
    private bool frontInteracting = false;
    private bool rearInteracting = false;

    private ServerPlayer frontInteractionPlayer;
    private ServerPlayer rearInteractionPlayer;

    private readonly ControlLeases<ServerPlayer> leases = new();

    // Other players' handcar handles (HandcarDrivePatch adds their strokes up); the host's own
    // handle is its ports.
    private HandcarPumpers<ServerPlayer> handcarPumpers;
    private Func<ServerPlayer, bool> outOfReach;

    private readonly Dictionary<TrainDamage, TrainDamage.HealthChanged> trainDamageDelegates = [];
    private readonly Dictionary<string, float> lastSentTrainDamages = [];

    private bool mainResPressureDirty;
    private bool brakeOverheatDirty;
    private float lastSentMainReservoirPressure = float.NaN;
    private float lastSentBrakePipePressure = float.NaN;
    private float lastSentBrakeCylinderPressure = float.NaN;
    private uint lastBrakeStateTick;

    private bool cargoStateDirty;
    private bool cargoHealthDirty;
    private bool cargoIsLoading;
    private bool carHealthDirty;
    private bool sendCouplers;
    private bool sendCables;
    private bool topologyDirty;

    // Where a derailed car was when it last moved more than DerailedSettleDistance.
    private const float DerailedSettleDistance = 0.02f;
    private Vector3 derailedAnchor;
    private uint derailedAnchorTick;
    private bool hasDerailedAnchor;

    public NetworkedTrainCarServer(NetworkedTrainCar car)
    {
        this.car = car;
        car.Client_Initialized = true; // the server's car follows nobody
        NetworkTrainsetWatcher.Instance.CheckInstance(); // Ensure the NetworkTrainsetWatcher is initialized
    }

    /// <summary>The server's half of a car; null on a car made when this game wasn't the server.</summary>
    public static NetworkedTrainCarServer Of(NetworkedTrainCar car) => car?.ServerPart as NetworkedTrainCarServer;

    public void Start()
    {
        if (!NetworkRole.IsServer) // the server stopped since the car was made
            return;

        if (car.handcarDrive != null)
        {
            handcarPumpers = new HandcarPumpers<ServerPlayer>();
            outOfReach = player => (player.WorldPosition - car.transform.position).sqrMagnitude > car.CarLengthSq;
            HandcarDrivePatch.Register(car.handcarDrive, handcarPumpers);
        }

        NetworkServer.Instance.PlayerDisconnected += OnPlayerDisconnect;

        car.bogie1.TrackChanged += BogieTrackChanged;
        car.bogie2.TrackChanged += BogieTrackChanged;

        TrainCar.frontCoupler.Uncoupled += CouplerUncoupled;
        TrainCar.rearCoupler.Uncoupled += CouplerUncoupled;
        TrainCar.frontCoupler.Coupled += CouplerCoupled;
        TrainCar.rearCoupler.Coupled += CouplerCoupled;
        foreach (Coupler coupler in TrainCar.couplers ?? Array.Empty<Coupler>())
            if (coupler.ChainScript != null)
                coupler.ChainScript.StateChanged += ChainStateChanged;

        TrainCar.CarDamage.CarEffectiveHealthStateUpdate += CarHealthUpdate;

        //find all TrainDamages and subscribe
        if (car.TryGetComponent<DamageController>(out DamageController damageController) && damageController != null)
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
                    void DamagesUpdate(float health) => TrainDamagesHealthUpdate(fieldName, health);

                    //subscribe to the event
                    trainDamageFields[i].Damage.HealthPercentageChanged += DamagesUpdate;

                    //store delegates and set a last sent value to an impossible value
                    trainDamageDelegates.Add(fieldValue, DamagesUpdate);
                    lastSentTrainDamages.Add(fieldName, -1f);
                }
            }
        }

        if (car.brakeSystem != null)
        {
            car.brakeSystem.MainResPressureChanged += MainResUpdate;
            car.brakeSystem.heatController.OverheatingActiveStateChanged += BrakeHeatUpdate;
        }

        car.StartCoroutine(WaitForLogicCar());
    }

    public void Unregister()
    {
        if (car.handcarDrive != null && handcarPumpers != null)
            HandcarDrivePatch.Unregister(car.handcarDrive);
    }

    public void Disable()
    {
        if (!NetworkRole.IsServer) // the server stopped first (the host leaving)
            return;

        if (!UnloadWatcher.isUnloading)
            NetworkServer.Instance?.CommitObservedTrainTopology();

        NetworkServer.Instance.PlayerDisconnected -= OnPlayerDisconnect;

        car.bogie1.TrackChanged -= BogieTrackChanged;
        car.bogie2.TrackChanged -= BogieTrackChanged;

        TrainCar.frontCoupler.Uncoupled -= CouplerUncoupled;
        TrainCar.rearCoupler.Uncoupled -= CouplerUncoupled;
        TrainCar.frontCoupler.Coupled -= CouplerCoupled;
        TrainCar.rearCoupler.Coupled -= CouplerCoupled;
        foreach (Coupler coupler in TrainCar.couplers ?? Array.Empty<Coupler>())
            if (coupler.ChainScript != null)
                coupler.ChainScript.StateChanged -= ChainStateChanged;

        TrainCar.CarDamage.CarEffectiveHealthStateUpdate -= CarHealthUpdate;

        //Unsubscribe from damage updates
        if (trainDamageDelegates != null && lastSentTrainDamages.Count > 0)
            foreach (var kvp in trainDamageDelegates)
                kvp.Key.HealthPercentageChanged -= kvp.Value;

        if (car.brakeSystem != null)
        {
            car.brakeSystem.MainResPressureChanged -= MainResUpdate;
            car.brakeSystem.heatController.OverheatingActiveStateChanged -= BrakeHeatUpdate;
        }

        if (TrainCar.logicCar != null)
        {
            TrainCar.logicCar.CargoLoaded -= OnCargoLoaded;
            TrainCar.logicCar.CargoUnloaded -= OnCargoUnloaded;
        }
    }

    private IEnumerator WaitForLogicCar()
    {
        while (TrainCar.logicCar == null)
            yield return null;

        TrainCar.logicCar.CargoLoaded += OnCargoLoaded;
        TrainCar.logicCar.CargoUnloaded += OnCargoUnloaded;

        if (TrainCar.CargoDamage)
            TrainCar.CargoDamage.CargoEffectiveHealthStateUpdate += CargoHealthUpdate;

        DirtyAllState();
    }

    public void DirtyAllState()
    {
        car.handbrakeDirty = true;
        mainResPressureDirty = true;
        cargoStateDirty = true;
        cargoHealthDirty = true;
        cargoIsLoading = true;
        carHealthDirty = true;
        sendCouplers = true;
        sendCables = true;
        ResyncPose();

        if (!car.hasSimFlow)
            return;
        foreach (string portId in car.simulationFlow.fullPortIdToPort.Keys)
        {
            var netId = NetworkedTrainCar.GetPortNetId(portId);
            car.dirtyPorts.Add(netId);
        }

        foreach (string fuseId in car.simulationFlow.fullFuseIdToFuse.Keys)
        {
            var netId = NetworkedTrainCar.GetFuseNetId(fuseId);
            car.dirtyFuses.Add(netId);
        }
    }

    /// <summary>
    /// Send the car's pose and both bogies' tracks and spans on the next tick, reliably, even if
    /// it's standing still (a stopped set is otherwise only resent every couple of seconds).
    /// </summary>
    public void ResyncPose()
    {
        car.BogieTracksDirty = true; // makes the set's packet reliable
        car.TicksSinceSync = uint.MaxValue / 2; // full pose on the next tick (a client rerailing to it needs one); not MaxValue, the tick's ++ would wrap it to 0
    }

    /// <summary>
    /// The host is rerailing this car or moving it to another track: once vanilla has (a physics
    /// step later), send clients where it is now, so their followers aren't left aiming at
    /// where it was.
    /// </summary>
    public void ResyncAfterRelocation() => CoroutineManager.Instance.StartCoroutine(ResyncAfterRelocationCoro());

    private IEnumerator ResyncAfterRelocationCoro()
    {
        yield return new WaitForFixedUpdate();
        for (float t0 = Time.realtimeSinceStartup; TrainCar != null && TrainCar.IsTeleporting && Time.realtimeSinceStartup - t0 < 5f;)
            yield return null;
        if (car != null && TrainCar != null)
            ResyncPose();
    }

    /// <summary>Whether a derailed car has moved less than 2 cm in the last second.</summary>
    public bool DerailedSettled(Vector3 position, uint tick)
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

    public bool ValidateClientSimFlowPacket(ServerPlayer player, CommonTrainPortsPacket packet)
    {
        // Only allow control ports to be updated by clients
        if (car.hasSimFlow)
            foreach (uint portNetId in packet.PortIds)
            {

                var portId = NetworkedTrainCar.GetPort(portNetId);
                if (car.simulationFlow.TryGetPort(portId, out Port _))
                {
                    // Only controls players drive: the others are the host's simulation's to set.
                    bool holdsLease = leases.Holds(player, portNetId);
                    if (car.portWriters == null || !car.portWriters.HostAccepts(portNetId, holdsLease))
                    {
                        NetworkServer.Instance.LogWarning($"Player {player.Username} tried to send a port players don't drive ({car.portWriters?.Classify(portNetId)})! ([{portId}, {portNetId}] on [{CurrentID}, {NetId}])");
                        car.Common_DirtyPorts(packet.PortIds);
                        return false;
                    }
                }
                else
                {
                    NetworkServer.Instance.LogWarning($"Player {player.Username} sent unknown portId: {portNetId}");
                    return false;
                }
            }

        // Only allow the player to update ports on the car they are in/near
        if (player.CarId == packet.NetId)
            return true;

        // Some ports can be updated by the player even if they are not in the car, like doors and windows.
        // Only deny the request if the player is more than 5 meters away from any point of the car.
        if ((player.WorldPosition - car.transform.position).sqrMagnitude <= car.CarLengthSq)
            return true;

        NetworkServer.Instance.LogWarning($"Player {player.Username} tried to send a sim flow packet for a car they are not in!");
        car.Common_DirtyPorts(packet.PortIds);
        return false;
    }

    /// <summary>
    /// Whether a player may write these ports: nobody else holds a lease on any of them. A lease
    /// keeps a lever being held, or the whistle rope, from being fought over. A control that's only
    /// turned or clicked never asks for one (a VR valve, a cab window): requiring a lease refused
    /// every change to those, so the player saw it and nobody else did.
    /// </summary>
    public bool MayWritePorts(ServerPlayer player, IEnumerable<uint> portNetIds) => leases.MayWrite(player, portNetIds);

    public uint CommitControlRevision()
    {
        if (car.ControlRevision != uint.MaxValue)
            car.ControlRevision++;
        return car.ControlRevision;
    }

    /// <summary>
    /// A player's accepted control changes. Their handcar handle goes to the pumpers
    /// (<see cref="HandcarDrivePatch"/>): the host's handle ports are its own player's.
    /// </summary>
    public void ApplyPlayerPorts(ServerPlayer player, CommonTrainPortsPacket packet)
    {
        if (handcarPumpers == null || !packet.PortIds.Any(car.handcarHandlePorts.Contains))
        {
            car.Common_UpdatePorts(packet);
            return;
        }

        List<uint> portIds = new(packet.PortIds.Length);
        List<float> portValues = new(packet.PortIds.Length);
        for (int i = 0; i < packet.PortIds.Length; i++)
        {
            uint portNetId = packet.PortIds[i];
            float value = packet.PortValues[i];
            if (portNetId == car.handcarBarPortNetId)
                handcarPumpers.SetBar(player, value);
            else if (portNetId == car.handcarEngagedPortNetId)
                handcarPumpers.SetEngaged(player, value > 0f);
            else
            {
                portIds.Add(portNetId);
                portValues.Add(value);
            }
        }
        if (portIds.Count > 0)
            car.Common_UpdatePorts(new CommonTrainPortsPacket
            {
                NetId = NetId,
                PortIds = portIds.ToArray(),
                PortValues = portValues.ToArray()
            });
    }

    #region The host's own changes (ITrainCarRole)

    // Every port the host's simulation sets is the server's to send, not only the controls.
    public bool Reports(Port port, uint portNetId) => true;

    public void NoteLocalControlChange() { }

    // A full resync dirties every port. The host's handcar handle is its own player's.
    public uint[] PortsToSend(HashSet<uint> dirtyPorts, out List<uint> settling)
    {
        settling = null;
        return dirtyPorts.Where(id => !car.locallyFedPorts.Contains(id) && !car.handcarHandlePorts.Contains(id)).ToArray();
    }

    public bool SendsChange(bool viaRemote) => true;

    // The host's are its simulation's (its player's levers included): committed with this tick's
    // values, as the car read them.
    public void SendPorts(uint[] portIds, float[] portValues) =>
        NetworkServer.Instance.CommitHostControls(car, portIds, portValues);

    public void SendFuses(uint[] fuseIds, bool[] fuseValues) =>
        NetworkServer.Instance.CommitHostControls(car, fuseIds: fuseIds, fuseValues: fuseValues);

    public void SendHandbrake(bool viaRemote) => NetworkServer.Instance.CommitHostControls(car, handbrake: true);

    // Vanilla raises this before it sets the release, and clears it at the next brake update, so
    // the host commits it now: checked at the server's next poll, it could be gone already.
    public void SendBrakeCylinderReleased() =>
        NetworkServer.Instance.CommitHostControls(car, brakeCylinderReleased: true);

    // Values from players are applied to the server's car as they come.
    public bool TakeHostPortValue(uint portNetId, Port port, float value, uint hostTick) => false;

    #endregion

    public void SendPaintTheme(TrainCarPaint.Target target, uint themeNetId) =>
        NetworkServer.Instance.SendPaintThemeChange(car, target, themeNetId);

    private void BogieTrackChanged(RailTrack arg1, Bogie arg2)
    {
        car.BogieTracksDirty = true;
    }

    private void OnCargoLoaded(CargoType obj)
    {
        cargoStateDirty = true;
        cargoIsLoading = true;
    }

    private void OnCargoUnloaded()
    {
        cargoStateDirty = true;
        cargoIsLoading = false;
        car.CargoModelIndex = byte.MaxValue;
    }

    private void CargoHealthUpdate(float health)
    {
        cargoHealthDirty = true;
    }

    private void CarHealthUpdate(float health)
    {
        //Multiplayer.LogDebug(() => $"Server_CarHealthUpdate({health}) netId: {NetId}");
        carHealthDirty = true;
    }

    private void TrainDamagesHealthUpdate(string field, float health)
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

    private void MainResUpdate(float normalizedPressure, float pressure)
    {
        mainResPressureDirty = true;
    }

    private void BrakeHeatUpdate(bool overheatActive)
    {
        brakeOverheatDirty = true;
    }

    private void CouplerUncoupled(object _, UncoupleEventArgs args)
    {
        sendCouplers |= args.dueToBrokenCouple;
        topologyDirty = true;
    }

    private void CouplerCoupled(object _, CoupleEventArgs args) => topologyDirty = true;

    // A chain settling in the server's world: hooked on, parked or left hanging by the host's
    // player, or tightened or loosened with its screw. Not a coupler event, so it's observed here
    // and committed on the next tick, like a couple; the host's own client used to report these as
    // topology commands (TopologyObservationPatch has the hoses, cocks and MU cables). Another
    // player's request being applied commits once it's done.
    private void ChainStateChanged(ChainCouplerInteraction.State state)
    {
        if (state == ChainCouplerInteraction.State.Being_Dragged || !NetworkRole.IsServer ||
            UnloadWatcher.isUnloading || NetworkLifecycle.Instance.IsProcessingPacket ||
            car.IsAuthoritativeTopologyObservationSuppressed)
            return;
        topologyDirty = true;
    }

    /// <summary>
    /// The compressor's activation signal follows the brake system's decision (main reservoir
    /// below the threshold), which DV writes to the port only when that decision changes. Once
    /// the port held anything else (in playtest 2 it was 0 with the reservoir empty and the engine
    /// running: no air, so no brakes), nothing put it right. The host keeps them matched.
    /// </summary>
    private void KeepCompressorSignal()
    {
        if (car.compressorSignalPort == null || TrainCar.brakeSystem == null)
            return;
        float wanted = TrainCar.brakeSystem.compressorActivationSignal ? 1f : 0f;
        if (car.compressorSignalPort.Value != wanted)
            car.compressorSignalPort.ExternalValueUpdate(wanted);
    }

    public void Tick(uint tick)
    {
        if (UnloadWatcher.isUnloading)
            return;

        KeepCompressorSignal();
        // A pumper who walked off (or fell off) while holding the handle stops pushing.
        if (handcarPumpers?.EngagedCount > 0)
            handcarPumpers.RemoveWhere(outOfReach);
        SendBrakeStates(tick);
        SendCouplers();
        SendCables();
        SendCargoState();
        SendCargoHealthUpdate();
        SendCarHealthState();

        // Native Coupler events fire before Trainset.Merge/Split and brake-line work completes.
        // Commit on the following tick so the snapshot observes the completed transaction.
        if (topologyDirty)
        {
            topologyDirty = false;
            NetworkServer.Instance?.CommitObservedTrainTopology();
        }

        car.TicksSinceSync++; //keep track of last full sync
    }

    private void SendBrakeStates(uint tick)
    {
        var brakeSystem = car.brakeSystem;
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
        NetworkServer.Instance.SendBrakeState
        (
            NetId,
            main, pipe, cylinder,
            hc.overheatPercentage, hc.overheatReductionFactor, hc.temperature
        );
    }

    private void SendCouplers()
    {
        if (!sendCouplers)
            return;

        sendCouplers = false;
        NetworkServer.Instance?.CommitObservedTrainTopology();
    }

    private void SendCables()
    {
        if (!sendCables)
            return;
        sendCables = false;
        NetworkServer.Instance?.CommitObservedTrainTopology();
    }

    private void SendCargoState()
    {
        if (!cargoStateDirty)
            return;
        cargoStateDirty = false;
        if (cargoIsLoading && TrainCar.logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;

        NetworkServer.Instance.SendCargoState(car, cargoIsLoading, car.CargoModelIndex);
    }

    private void SendCargoHealthUpdate()
    {
        if (!cargoHealthDirty)
            return;

        cargoHealthDirty = false;

        if (TrainCar.logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;

        NetworkServer.Instance.SendCargoHealthUpdate(NetId, TrainCar.CargoDamage.currentHealth);
    }

    private void SendCarHealthState()
    {
        if (!carHealthDirty)
            return;
        carHealthDirty = false;
        NetworkServer.Instance.SendCarHealthUpdate(NetId, TrainCarHealthData.From(TrainCar));
    }

    public bool ValidateCouplerInteraction(CommonCouplerInteractionPacket packet, ServerPlayer player)
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

    public void ReceiveAuthorityRequest(uint portNetId, ServerPlayer player, bool requestAuthority,
        Vector3 playerPosition)
    {
        using IDisposable debugScope = DebugTrace.BeginApply("train", "train.authority-apply", DebugRuntimeSide.Server, "Control/Port", $"{NetId}:{portNetId}",
            () => DebugValueSnapshotter.SnapshotObject(new { carNetId = NetId, portNetId, playerId = player?.PlayerId, requestAuthority }));
        LeaseOutcome outcome = requestAuthority
            ? leases.Take(portNetId, player, (playerPosition - car.transform.position).sqrMagnitude <= car.CarLengthSq)
            : leases.Release(portNetId, player);
        NetworkServer server = NetworkServer.Instance;
        switch (outcome)
        {
            case LeaseOutcome.OutOfRange:
                DebugTrace.Validation("train", "Control/Port", $"{NetId}:{portNetId}", false, "sender-out-of-range", DebugRuntimeSide.Server);
                server.LogWarning($"Player \"{player.Username}\" attempted to gain authority for a control on car {CurrentID}, but they are too far away!");
                server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Denied, player);
                server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Released, player);
                break;
            case LeaseOutcome.Granted:
                // Communicate to all players
                server.LogDebug(() => $"Player \"{player.Username}\" granted authority for a control on car {CurrentID}");
                server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Blocked, excludePlayer: player);
                break;
            case LeaseOutcome.HeldByOther:
                DebugTrace.Validation("train", "Control/Port", $"{NetId}:{portNetId}", false, "authority-held-by-other-player", DebugRuntimeSide.Server);
                server.LogWarning($"Player \"{player.Username}\" attempted to gain authority for a control that's in use on car {CurrentID}");
                server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Denied, player);
                break;
            case LeaseOutcome.Released:
                server.LogDebug(() => $"Player \"{player.Username}\" released authority for a control on car {CurrentID}");
                server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Released);
                break;
            case LeaseOutcome.NotOwner:
                DebugTrace.Validation("train", "Control/Port", $"{NetId}:{portNetId}", false, "authority-release-not-owner", DebugRuntimeSide.Server);
                server.LogWarning($"Player \"{player.Username}\" attempted to release authority for a control that's not theirs on car {CurrentID}");
                server.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Denied, player);
                break;
        }
    }

    /// <summary>Who holds the lease on a control's port, if anyone.</summary>
    public ServerPlayer LeaseHolder(uint portNetId) => leases.HolderOf(portNetId);

    /// <summary>How many control leases are held on the car.</summary>
    public int LeaseCount => leases.Count;

    public void ValidatePaintThemeChange(TrainCarPaint.Target target, PaintTheme theme, ServerPlayer player)
    {
        var playerPos = TrainCar.transform.InverseTransformPoint(player.WorldPosition);
        Vector3 localClosestPoint = TrainCar.Bounds.ClosestPoint(playerPos);
        Vector3 worldClosestPoint = TrainCar.transform.TransformPoint(localClosestPoint);

        if ((player.WorldPosition - worldClosestPoint).sqrMagnitude > MAX_PAINT_DISTANCE_SQ)
        {
            NetworkServer.Instance.LogWarning($"Player \"{player.Username}\" attempted to change the paint theme for car {CurrentID}, but they are too far away!");

            var paintControllers = car.GetComponents<TrainCarPaint>();
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

            NetworkServer.Instance.SendPaintThemeChange(car, target, themeNetId, player);

            return;
        }

        car.Common_ReceivePaintThemeUpdate(target, theme);
    }

    private void OnPlayerDisconnect(ServerPlayer player)
    {
        handcarPumpers?.Remove(player);

        //todo: resolve player disconnection during chain interaction
        if (frontInteractionPlayer == player || rearInteractionPlayer == player)
        {
            Multiplayer.LogWarning($"Server_OnPlayerDisconnect() Coupler interaction in unknown state [{CurrentID}, {NetId}] isFront: {frontInteractionPlayer == player}");
            if (frontInteractionPlayer == player)
            {
                frontInteracting = false;
                //NetworkClient.Instance.SendCouplerInteraction(cou, coupler, otherCoupler);
            }
            else
            {
                rearInteracting = false;
            }
        }

        // Clean up blocked controls
        foreach (uint portNetId in leases.ReleaseAll(player))
            NetworkServer.Instance.SendTrainControlAuthorityUpdate(NetId, portNetId, ControlAuthorityState.Released);
    }
}

public static class NetworkedTrainCarServerExtensions
{
    /// <summary>The server's half of the car (<see cref="NetworkedTrainCarServer"/>).</summary>
    public static NetworkedTrainCarServer Server(this NetworkedTrainCar car) => NetworkedTrainCarServer.Of(car);
}
