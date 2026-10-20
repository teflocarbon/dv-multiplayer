using DV.CashRegister;
using DV.Optimizers;
using DV.ThingTypes;
using Multiplayer.Server.Players;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Stations;

/// <summary>
/// The server's part of a service station (beside its <see cref="NetworkedPitStopStation"/>): knows
/// which players are at the station, sends a player arriving the station's state, applies and
/// relays players' interactions, sends the units bought while a resource flows, and sets up the
/// station's register and plugs on the server. Added by <c>NetworkServer.InitialiseStations</c>.
/// </summary>
public class ServerPitStopStation : MonoBehaviour
{
    const float DEFAULT_DISABLER_SQR_DISTANCE = 250000f;
    const float DEFAULT_DISABLER_INTERVAL = 2f;
    const float NEARBY_REMOVAL_DELAY = 3f;

    public NetworkedPitStopStation Station { get; private set; }
    public CullingManager CullingManager { get; private set; }

    private readonly Dictionary<LocoResourceModule, (Action FillStart, Action FillStop, Action DrainStart, Action DrainStop)> resourceStartStopDelegates = [];
    private readonly Dictionary<LocoResourceModule, (bool isFlowing, bool wasFlowing, bool lastUpdate)> resourceFlowing = [];

    private bool carEnteredSubscribed;

    private static NetworkServer Server => NetworkServer.Instance;

    public static bool TryGet(ushort netId, out ServerPitStopStation server)
    {
        server = null;
        return NetworkedPitStopStation.Get(netId, out NetworkedPitStopStation station) && station.TryGetComponent(out server);
    }

    #region Setup
    private bool setUp;

    /// <summary>
    /// Sets the station up on the server. Called once, by hand: a station far from the host can be
    /// inactive, and Unity only runs Awake and Start once it's active.
    /// </summary>
    public void Setup()
    {
        if (setUp)
            return;
        setUp = true;

        Station = GetComponent<NetworkedPitStopStation>();

        // Setup culling
        var disabler = GetComponentInParent<PlayerDistanceGameObjectsDisabler>();

        var cullingSqrDistance = DEFAULT_DISABLER_SQR_DISTANCE;
        var cullingCheckInterval = DEFAULT_DISABLER_INTERVAL;

        if (disabler != null)
        {
            cullingSqrDistance = disabler.disableSqrDistance;
            cullingCheckInterval = disabler.checkPeriodPerGO;
        }

        var activationSqrDistance = cullingSqrDistance / 2;

        CullingManager = new(cullingCheckInterval, cullingSqrDistance, activationSqrDistance, NEARBY_REMOVAL_DELAY, gameObject);
        CullingManager.PlayerEnteredActivationRegion += OnPlayerEnteredActivationRegion;
        CullingManager.PlayerEnteredCullingRegion += OnPlayerEnteredCullingRegion;

        // Setup network events
        NetworkLifecycle.Instance.OnTick += OnTick;

        Server.PlayerDisconnected += OnPlayerDisconnect;

        CoroutineManager.Instance.StartCoroutine(Init());
    }

    protected void OnDestroy()
    {
        foreach (var kvp in resourceStartStopDelegates)
        {
            var (fillStart, fillStop, drainStart, drainStop) = kvp.Value;
            kvp.Key.FillStarted -= fillStart;
            kvp.Key.FillStopped -= fillStop;
            kvp.Key.DrainStarted -= drainStart;
            kvp.Key.DrainStopped -= drainStop;
        }

        resourceStartStopDelegates.Clear();

        if (CullingManager != null)
        {
            CullingManager.PlayerEnteredActivationRegion -= OnPlayerEnteredActivationRegion;
            CullingManager.PlayerEnteredCullingRegion -= OnPlayerEnteredCullingRegion;
            CullingManager.Dispose();
        }

        if (NetworkLifecycle.Instance != null)
            NetworkLifecycle.Instance.OnTick -= OnTick;

        if (Server != null)
            Server.PlayerDisconnected -= OnPlayerDisconnect;

        // Monitor changes to vehicles in the pit stop
        if (carEnteredSubscribed && Station?.Station?.pitstop != null)
            Station.Station.pitstop.CarEntered -= OnCarPitStopEntered;
    }
    #endregion

    /// <summary>
    /// The station's server-side setup, alongside the station's own <c>Init</c>: cars entering, the
    /// register, the resource modules' flow, and the plugs.
    /// </summary>
    private IEnumerator Init()
    {
        while (Station?.Station?.pitstop == null)
            yield return new WaitForEndOfFrame();

        // Monitor changes to vehicles in the pit stop
        Station.Station.pitstop.CarEntered += OnCarPitStopEntered;
        carEnteredSubscribed = true;

        // Ensure any cars already in the pit stop have external interactables loaded
        if (Station.Station.pitstop.carList.Count > 0)
            OnCarPitStopEntered();

        // Wait for cash registers to load
        CashRegisterWithModules register = null;
        yield return new WaitUntil(() => (register = transform.parent.GetComponentInChildren<CashRegisterWithModules>(true)) != null);

        Multiplayer.LogDebug(() => $"ServerPitStopStation.Init() Waiting for NetworkedCashRegisterWithModules {Station.StationName}");

        NetworkedCashRegisterWithModules netRegister = null;
        yield return new WaitUntil(() => NetworkedCashRegisterWithModules.TryGet(register, out netRegister) && netRegister != null);

        Multiplayer.LogDebug(() => $"ServerPitStopStation.Init() Initialising Cash Register for station {Station.StationName}");
        if (netRegister.TryGetComponent(out ServerCashRegister serverRegister))
            serverRegister.Init(CullingManager);

        // Subscribe to fill/drain stop events once the station has its modules
        yield return new WaitUntil(() => Station.IsInitialised);

        foreach (var resourceModule in Station.ResourceModules)
        {
            void FillStartHandler() => OnFlowStarted(resourceModule);
            void FillStopHandler() => OnFlowStopped(resourceModule);
            void DrainStartHandler() => OnFlowStarted(resourceModule);
            void DrainStopHandler() => OnFlowStopped(resourceModule);

            resourceModule.FillStarted += FillStartHandler;
            resourceModule.FillStopped += FillStopHandler;
            resourceModule.DrainStarted += DrainStartHandler;
            resourceModule.DrainStopped += DrainStopHandler;

            resourceStartStopDelegates[resourceModule] = (FillStartHandler, FillStopHandler, DrainStartHandler, DrainStopHandler);
        }

        foreach (var plug in Station.Plugs)
            plug.GetOrAddComponent<ServerPluggableObject>().Init(this);
    }

    public bool ValidateInteraction(CommonPitStopInteractionPacket packet, ServerPlayer player)
    {
        //todo: implement validation code (player distance, player interacting, etc.)
        return true;
    }

    //todo: update when merged with ModAPI branch
    public void OnPlayerDisconnect(ServerPlayer player)
    {
        //todo: when a player disconnects, if they are interacting with a lever, cancel the interaction
        //Multiplayer.LogWarning($"OnPlayerDisconnect()");
    }

    public void OnPlayerEnteredActivationRegion(ServerPlayer player)
    {
        PitStopStation pitStop = Station.Station;

        // Ensure all resource data exists
        Station.InitialiseData();

        // One struct per module type
        int resourceCount = pitStop.locoResourceModules.resourceModules.Length;
        LocoResourceModuleData[] stateData = new LocoResourceModuleData[resourceCount];

        Multiplayer.LogDebug(() => $"ServerPitStopStation.OnPlayerEnteredActivationRegion() [{Station.StationName}, {Station.NetId}] player: {player.Username}, car count: {pitStop.pitstop.carList.Count}, resourceCount: {resourceCount}");
        int i;
        for (i = 0; i < resourceCount; i++)
        {
            int index = i;
            Multiplayer.LogDebug(() => $"ServerPitStopStation.OnPlayerEnteredActivationRegion() [{Station.StationName}, {Station.NetId}] player: {player.Username}, i: {index}, data count: {pitStop.locoResourceModules.resourceModules[index].resourceData.Count}");
            stateData[i] = LocoResourceModuleData.From(pitStop.locoResourceModules.resourceModules[i]);
        }

        // Car selection and lever states
        int carIndex = pitStop.pitstop.SelectedIndex;

        NetworkedPluggableObject[] plugs = Station.Plugs.ToArray();
        PitStopPlugData[] plugData = new PitStopPlugData[plugs.Length];

        for (i = 0; i < plugs.Length; i++)
        {
            NetworkedPluggableObject plug = plugs[i];
            Multiplayer.LogDebug(() => $"ServerPitStopStation.OnPlayerEnteredActivationRegion() [{Station.StationName}, {Station.NetId}] player: {player.Username}, plug netId: {plug.NetId}");
            plugData[i] = PitStopPlugData.From(plug, true);
        }

        int faucetPos = Station.FaucetNotch;
        if (faucetPos < 0)
            Multiplayer.LogWarning($"ServerPitStopStation.OnPlayerEnteredActivationRegion() [{Station.StationName}] faucetCrankSteppedJoint is null");

        // Send current state
        Server.SendPitStopBulkDataPacket(Station.NetId, pitStop.pitstop.carList.Count, carIndex, faucetPos, stateData, plugData, player);
    }

    public void OnPlayerEnteredCullingRegion(ServerPlayer player)
    {
        //todo: when a player leaves the region cancel any interactions
        //Multiplayer.LogWarning($"OnPlayerDisconnect()");
    }

    public void ProcessInteraction(CommonPitStopInteractionPacket packet, ServerPlayer senderPlayer)
    {
        Multiplayer.LogDebug(() => $"ServerPitStopStation.ProcessInteraction() from: {senderPlayer.Username}, tick: {packet.Tick}, id: {senderPlayer.PlayerId}");

        if (ValidateInteraction(packet, senderPlayer))
        {
            // Ensure colliders for water, coal, etc. are loaded
            OnCarPitStopEntered();

            Station.ProcessingAsHost = true;
            // The host player's own interaction has happened in this world already.
            if (senderPlayer != Server.LocalPlayer)
            {
                Multiplayer.LogDebug(() => $"ServerPitStopStation.ProcessInteraction() ProcessInteractionPacketAsClient()");
                Station.ProcessInteractionPacketAsClient(packet);
            }
            Station.ProcessingAsHost = false;

            // Send to all other players
            foreach (var player in CullingManager.ActivePlayers)
            {
                if (player.PlayerId != senderPlayer.PlayerId)
                {
                    Multiplayer.LogDebug(() => $"ServerPitStopStation.ProcessInteraction() sending to player: {player.Username}");
                    Server.SendPitStopInteractionPacket(player, packet);
                }
            }
        }
        else
        {
            Multiplayer.LogDebug(() => $"ServerPitStopStation.ProcessInteraction() failed validation");
            // Failed to validate, player needs to rollback interaction (not the host's player: its
            // world is this one)
            if (senderPlayer != Server.LocalPlayer)
                Server.SendPitStopInteractionPacket(
                    senderPlayer,
                    new CommonPitStopInteractionPacket
                    {
                        NetId = packet.NetId,
                        InteractionType = PitStopStationInteractionType.Reject
                    }
                );
        }
    }

    private void OnFlowStarted(LocoResourceModule module)
    {
        Multiplayer.LogDebug(() => $"ServerPitStopStation.OnFlowStarted() {module.resourceType} [{Station.StationName}, {Station.NetId}]");
        resourceFlowing[module] = (isFlowing: true, wasFlowing: false, lastUpdate: false);
    }

    private void OnFlowStopped(LocoResourceModule module)
    {
        Multiplayer.LogDebug(() => $"ServerPitStopStation.OnFlowStopped() {module.resourceType} [{Station.StationName}, {Station.NetId}]");

        resourceFlowing[module] = (isFlowing: false, wasFlowing: true, lastUpdate: false);
        SendResourceUpdate(module);
    }

    private void OnTick(uint tick)
    {
        var modules = resourceFlowing.Keys.ToList();
        foreach (var module in modules)
        {
            // Ensure the final value is sent, we need perfect sync for payments to work
            if (resourceFlowing[module].isFlowing || resourceFlowing[module].wasFlowing)
            {
                SendResourceUpdate(module);

                if (!resourceFlowing[module].isFlowing)
                {
                    // We want one final update to ensure race conditions between flow stopping and game ticks do not cause sync issues
                    if (!resourceFlowing[module].lastUpdate)
                        resourceFlowing[module] = (isFlowing: false, wasFlowing: true, lastUpdate: true);
                    else
                        resourceFlowing[module] = (isFlowing: false, wasFlowing: false, lastUpdate: false);
                }
            }
        }
    }

    private void SendResourceUpdate(LocoResourceModule module)
    {
        Multiplayer.LogDebug(() => $"ServerPitStopStation.SendResourceUpdate({module.resourceType}) [{Station.StationName}, {Station.NetId}], active players: {CullingManager.ActivePlayers.Count}");

        CommonPitStopInteractionPacket packet = new()
        {
            Tick = NetworkLifecycle.Instance.Tick,
            NetId = Station.NetId,
            InteractionType = PitStopStationInteractionType.ResourceUpdate,
            ResourceType = (int)module.resourceType,
            Value = module.Data.unitsToBuy
        };

        Station.NoteUnitsSent(module.resourceType, module.Data.unitsToBuy);

        foreach (var player in CullingManager.ActivePlayers)
        {
            if (player != null)
            {
                Multiplayer.LogDebug(() => $"ServerPitStopStation.SendResourceUpdate({module.resourceType}) [{Station.StationName}, {Station.NetId}], sending to peer: {player.Username}, value: {module.Data.unitsToBuy}, flowing: {module.IsFlowing}");
                Server.SendPitStopInteractionPacket(player, packet);
            }
            else
            {
                Multiplayer.LogWarning($"ServerPitStopStation.SendResourceUpdate({module.resourceType}) [{Station.StationName}, {Station.NetId}], player is null, skipping send");
            }
        }
    }

    private void OnCarPitStopEntered()
    {
        foreach (var car in Station.Station.pitstop.carList)
        {
            if (car == null)
                continue;

            if (!car.AreExternalInteractablesLoaded && !car.AreDummyExternalInteractablesLoaded)
            {
                Multiplayer.LogDebug(() => $"ServerPitStopStation.OnCarPitStopEntered() [{Station.StationName}, {Station.NetId}] Loading dummy external interactables for car: {car.ID}");
                car.LoadDummyExternalInteractables();
            }
        }
    }
}
