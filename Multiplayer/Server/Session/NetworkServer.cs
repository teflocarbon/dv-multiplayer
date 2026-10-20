using DV;
using DV.Customization;
using DV.Customization.Paint;
using DV.Garages;
using DV.InventorySystem;
using DV.LocoRestoration;
using DV.Logic.Job;
using DV.Scenarios.Common;
using DV.ServicePenalty;
using DV.Shops;
using DV.CabControls;
using DV.ThingTypes;
using DV.WeatherSystem;
using DV.Utils;
using Humanizer;
using LiteNetLib;
using LiteNetLib.Utils;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.Server.Api;
using Multiplayer.Server.Career;
using Multiplayer.Server.Chat;
using Multiplayer.Server.Coverage;
using Multiplayer.Server.Dedicated;
using Multiplayer.Server.Items;
using Multiplayer.Server.Jobs;
using Multiplayer.Server.Trains;
using Multiplayer.Server.World;
using Multiplayer.Shared.Api;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Protocol.Data.Player;
using Multiplayer.Shared.Protocol.Data.RPCs;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Clientbound.SaveGame;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Train;
using Multiplayer.Shared.Protocol.Packets.Unconnected;
using Multiplayer.Shared.Protocol.Serialization;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Transport;
using Multiplayer.Shared.Utils;
using Multiplayer.Shared.World;
using Multiplayer.Core.Containers;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Trains;
using Multiplayer.Core.World;
using ItemMoveReason = Multiplayer.Core.Items.ItemMoveReason;
using ItemRemovalReason = Multiplayer.Core.Items.ItemRemovalReason;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using UnityEngine;

namespace Multiplayer.Server.Session;

public partial class NetworkServer : NetworkManager
{
    /// <summary>The server this game runs (a player host or a dedicated host); null when it isn't the server. Set by the session root.</summary>
    public static NetworkServer Instance { get; internal set; }

    private const int WEATHER_UPDATE_INTERVAL = 30; //seconds
    private const int HIGH_PING_LOG_INTERVAL = 60; //only log high ping once every 60 seconds per player

    public Action<ServerPlayer> PlayerConnected;
    public Action<ServerPlayer> PlayerDisconnected;
    public Action<ServerPlayer> PlayerReady;
    protected override string LogPrefix => "[Server]";
    protected override DebugRuntimeSide DebugSide => DebugRuntimeSide.Server;

    private readonly Queue<ITransportPeer> joinQueue = new();   //Queue for players attempting to join while server is loading
    private readonly Queue<ITransportPeer> gameDataQueue = new(); // players waiting for game data while the server loads

    private readonly Dictionary<byte, ServerPlayer> serverPlayers = [];             //player Id to ServerPlayer mapping
    private readonly Dictionary<byte, ITransportPeer> peers = [];                   //player Id to peer mapping
    private readonly Dictionary<ITransportPeer, ServerPlayer> peerToPlayer = [];    //peer to ServerPlayer mapping
    public readonly Dictionary<byte, ServerPlayerWrapper> PlayerWrapperCache = []; //cache for ServerPlayers for API use

    private LobbyServerManager lobbyServerManager;
    public readonly bool IsSinglePlayer;
    public LobbyServerData ServerData;
    public RerailController rerailController;

    private bool fastTravelAdvancesTime;
    /// <summary>Whether fast travel advances everyone's clock (the host's setting); sleeping always does.</summary>
    public bool FastTravelAdvancesTime => fastTravelAdvancesTime;

    public IReadOnlyCollection<ServerPlayer> ServerPlayers => serverPlayers.Values;
    public IReadOnlyCollection<ServerPlayerWrapper> ServerPlayerWrappers => PlayerWrapperCache.Values;
    public int PlayerCount => ServerPlayers.Count;


    /// <summary>The host's own player's peer (<see cref="LocalPlayer"/>); null until it's admitted.</summary>
    public ITransportPeer SelfPeer => LocalPlayer?.Peer;

    /// <summary>The host's own player's id (<see cref="LocalPlayer"/>); 0 until it's admitted.</summary>
    public byte SelfId => LocalPlayer?.PlayerId ?? 0;

    public readonly IDifficulty Difficulty;
    private bool IsLoaded;

    private readonly ChatManager _chatManager = new();
    private readonly PlayerAuthentication authentication;
    public ChatManager ChatManager => _chatManager;

    private uint lastTick;
    private uint nextTrainBaselineId;
    private readonly TrainTopologyAuthority trainTopologyAuthority = new();
    private uint lastBroadcastTrainTopologyRevision;
    private bool trainTopologyCommitPending;
    private readonly HashSet<ushort> pendingTrainTopologyControlCars = new();
    private uint nextItemRecallOperationId;
    private readonly Dictionary<uint, PendingItemRecall> pendingItemRecalls = new();
    private readonly Dictionary<ushort, uint> pendingItemRecallByItem = new();
    private const uint ItemRecallTimeoutTicks = NetworkLifecycle.TICK_RATE * 5;

    public NetworkServer(IDifficulty difficulty, Settings settings, bool singlePlayer, LobbyServerData serverData) : base(settings)
    {
        Log($"Server created for {(singlePlayer ? "single player" : "multiplayer")} game");
        HazmatServer.Reset();
        global::Multiplayer.Server.Trains.CargoFireServer.Clear();

        IsSinglePlayer = singlePlayer;
        ServerData = serverData;
        Difficulty = difficulty;

        fastTravelAdvancesTime = settings.FastTravelAdvancesTime;
        authentication = new PlayerAuthentication(this);
    }

    public override void OnSettingsUpdated(Settings settings)
    {
        base.OnSettingsUpdated(settings);

        if (settings.FastTravelAdvancesTime != fastTravelAdvancesTime)
        {
            fastTravelAdvancesTime = settings.FastTravelAdvancesTime;
            SendGameParams(Globals.G.GameParams);
        }
    }

    public override bool Start(int port)
    {
        Log($"Starting server...");

        //setup paint theme lookup cache
        PaintThemeLookup.Instance.CheckInstance();
        // MPAPI's NetId lookups: only a client's Start reached this, so a host without one had none.
        NetIdProvider.Instance.CheckInitialization();

        WorldStreamingInit.LoadingFinished += OnLoaded;

        //Try to get our static IPv6 Address we will need this for IPv6 NAT punching to be reliable
        bool started;
        if (IPAddress.TryParse(LobbyServerManager.GetStaticIPv6Address(), out IPAddress ipv6Address))
        {
            //start the connection, IPv4 messages can come from anywhere, IPv6 messages need to specifically come from the static IPv6
            started = base.Start(IPAddress.Any, ipv6Address, port);
        }
        else
        {
            //we're not running IPv6, start as normal
            started = base.Start(port);
        }

        if (!started)
        {
            // Otherwise the next world load would start ticking a server that never ran.
            WorldStreamingInit.LoadingFinished -= OnLoaded;
            return false;
        }

        return true;
    }

    public override void Stop()
    {
        Log($"Stopping server...");
        WorldStreamingInit.LoadingFinished -= OnLoaded;
        DedicatedHostSaveLock.Release();

        if (lobbyServerManager != null)
        {
            lobbyServerManager.RemoveFromLobbyServer();
            UnityEngine.Object.Destroy(lobbyServerManager);
        }

        //Alert all clients (except host)
        var packet = WritePacket(new ClientboundDisconnectPacket());
        foreach (var peer in peers.Values)
        {
            if (peer != SelfPeer)
                peer?.Disconnect(packet);
        }

        //Reset player ID pool
        foreach (var player in serverPlayers.Values)
            player.Dispose();
        authentication.Dispose();

        NetworkLifecycle.Instance.OnTick -= OnTick;

        base.Stop();
    }

    protected override void Subscribe()
    {
        // Client management
        netPacketProcessor.SubscribeReusable<ServerboundClientLoginPacket, IConnectionRequest>(OnServerboundClientLoginPacket);
        netPacketProcessor.SubscribeReusable<CommonChatPacket, ITransportPeer>(OnCommonChatPacket);
        netPacketProcessor.SubscribeReusable<UnconnectedPingPacket, IPEndPoint>(OnUnconnectedPingPacket);


        // World sync
        netPacketProcessor.SubscribeReusable<ServerboundLoadStateUpdatePacket, ITransportPeer>(OnServerboundLoadStateUpdatePacket);
        netPacketProcessor.SubscribeReusable<ServerboundTimeAdvancePacket, ITransportPeer>(OnServerboundTimeAdvancePacket);

        netPacketProcessor.SubscribeReusable<CommonChangeJunctionPacket, ITransportPeer>(OnCommonChangeJunctionPacket);
        netPacketProcessor.SubscribeReusable<CommonRotateTurntablePacket, ITransportPeer>(OnCommonRotateTurntablePacket);

        netPacketProcessor.SubscribeReusable<CommonPitStopInteractionPacket, ITransportPeer>(OnCommonPitStopInteractionPacket);
        netPacketProcessor.SubscribeNetSerializable<CommonPitStopPlugInteractionPacket, ITransportPeer>(OnCommonPitStopPlugInteractionPacket);

        netPacketProcessor.SubscribeReusable<CommonCashRegisterWithModulesActionPacket, ITransportPeer>(OnCommonCashRegisterWithModulesActionPacket);

        netPacketProcessor.SubscribeReusable<CommonGenericSwitchStatePacket, ITransportPeer>(OnCommonGenericSwitchStatePacket);


        // Player
        netPacketProcessor.SubscribeReusable<ServerboundPlayerPositionPacket, ITransportPeer>(OnServerboundPlayerPositionPacket);
        netPacketProcessor.SubscribeReusable<ServerboundLicensePurchaseRequestPacket, ITransportPeer>(OnServerboundLicensePurchaseRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundPayFeesRequestPacket, ITransportPeer>(OnServerboundPayFeesRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundPlayerPreferenceUpdatePacket, ITransportPeer>(OnServerboundPlayerPreferenceUpdatePacket);


        // Train
        netPacketProcessor.SubscribeReusable<ServerboundTrainSyncRequestPacket, ITransportPeer>(OnServerboundTrainSyncRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainTopologyCommandPacket, ITransportPeer>(OnServerboundTrainTopologyCommandPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainTopologySyncRequestPacket, ITransportPeer>(OnServerboundTrainTopologySyncRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTenderCoalPacket, ITransportPeer>(OnServerboundTenderCoalPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainPortsPacket, ITransportPeer>(OnCommonTrainPortsPacket);
        netPacketProcessor.SubscribeReusable<CommonTrainFusesPacket, ITransportPeer>(OnCommonTrainFusesPacket);
        netPacketProcessor.SubscribeReusable<CommonPaintThemePacket, ITransportPeer>(OnCommonPaintThemePacket);

        // Train Interaction
        netPacketProcessor.SubscribeReusable<ServerboundTrainControlAuthorityPacket, ITransportPeer>(OnServerboundTrainControlAuthorityPacket);
        netPacketProcessor.SubscribeReusable<CommonCouplerInteractionPacket, ITransportPeer>(OnCommonCouplerInteractionPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainsetRelocationAckPacket, ITransportPeer>(OnServerboundTrainsetRelocationAckPacket);
        netPacketProcessor.SubscribeReusable<CommonBrakeCylinderReleasePacket, ITransportPeer>(OnCommonBrakeCylinderReleasePacket);
        netPacketProcessor.SubscribeReusable<CommonHandbrakePositionPacket, ITransportPeer>(OnCommonHandbrakePositionPacket);
        netPacketProcessor.SubscribeReusable<ServerboundAddCoalPacket, ITransportPeer>(OnServerboundAddCoalPacket);
        netPacketProcessor.SubscribeReusable<ServerboundFireboxIgnitePacket, ITransportPeer>(OnServerboundFireboxIgnitePacket);

        netPacketProcessor.SubscribeReusable<ServerboundTrainDeleteRequestPacket, ITransportPeer>(OnServerboundTrainDeleteRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainRerailRequestPacket, ITransportPeer>(OnServerboundTrainRerailRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundSandboxCargoPacket, ITransportPeer>(OnServerboundSandboxCargoPacket);
        netPacketProcessor.SubscribeReusable<ServerboundSandboxCarDamagePacket, ITransportPeer>(OnServerboundSandboxCarDamagePacket);
        netPacketProcessor.SubscribeReusable<ServerboundCargoIgnitePacket, ITransportPeer>(OnServerboundCargoIgnitePacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainSpawnRequestPacket, ITransportPeer>(OnServerboundTrainSpawnRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundWorkTrainRequestPacket, ITransportPeer>(OnServerboundWorkTrainRequestPacket);


        // Jobs
        netPacketProcessor.SubscribeReusable<ServerboundJobValidateRequestPacket, ITransportPeer>(OnServerboundJobValidateRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundJobBookletSummonRequestPacket, ITransportPeer>(OnServerboundJobBookletSummonRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundJobRerollRequestPacket, ITransportPeer>(OnServerboundJobRerollRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundWarehouseMachineControllerRequestPacket, ITransportPeer>(OnServerboundWarehouseMachineControllerRequestPacket);

        // Items
        //netPacketProcessor.SubscribeNetSerializable<CommonItemsBulkUpdatePacket, ITransportPeer>(OnCommonItemChangePacket);
        netPacketProcessor.SubscribeReusable<CommonItemUpdatePacket, ITransportPeer>(OnCommonItemUpdatePacket);
        netPacketProcessor.SubscribeReusable<ServerboundItemAdoptionPacket, ITransportPeer>(OnServerboundItemAdoptionPacket);
        netPacketProcessor.SubscribeReusable<ServerboundItemRecallPacket, ITransportPeer>(OnServerboundItemRecallPacket);
        netPacketProcessor.SubscribeReusable<ServerboundItemRecallPreparedPacket, ITransportPeer>(OnServerboundItemRecallPreparedPacket);
        netPacketProcessor.SubscribeReusable<ServerboundMoneyItemDepositPacket, ITransportPeer>(OnServerboundMoneyItemDepositPacket);
        netPacketProcessor.SubscribeReusable<ServerboundShopPaymentPacket, ITransportPeer>(OnServerboundShopPaymentPacket);
        netPacketProcessor.SubscribeReusable<ServerboundHazmatIgnitePacket, ITransportPeer>(OnServerboundHazmatIgnitePacket);
        netPacketProcessor.SubscribeReusable<ServerboundShopPurchasePacket, ITransportPeer>(OnServerboundShopPurchasePacket);
        netPacketProcessor.SubscribeReusable<ServerboundItemBelowMapPacket, ITransportPeer>(OnServerboundItemBelowMapPacket);
        netPacketProcessor.SubscribeReusable<ServerboundLostItemsRequestPacket, ITransportPeer>(OnServerboundLostItemsRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundLostItemRetrievePacket, ITransportPeer>(OnServerboundLostItemRetrievePacket);
        netPacketProcessor.SubscribeReusable<ServerboundContainerBrowsePacket, ITransportPeer>(OnServerboundContainerBrowsePacket);
        netPacketProcessor.SubscribeReusable<ServerboundContainerMutationPacket, ITransportPeer>(OnServerboundContainerMutationPacket);
        netPacketProcessor.SubscribeReusable<ServerboundWorldItemCataloguePacket, ITransportPeer>(OnServerboundWorldItemCataloguePacket);
        netPacketProcessor.SubscribeReusable<ServerboundWorldItemProjectionAckPacket, ITransportPeer>(OnServerboundWorldItemProjectionAckPacket);
        netPacketProcessor.SubscribeReusable<ServerboundItemSpatialSamplePacket, ITransportPeer>(OnServerboundItemSpatialSamplePacket);
        netPacketProcessor.SubscribeReusable<ServerboundItemSpatialSettlementPacket, ITransportPeer>(OnServerboundItemSpatialSettlementPacket);
        netPacketProcessor.SubscribeReusable<ServerboundItemTrainWakeWitnessPacket, ITransportPeer>(OnServerboundItemTrainWakeWitnessPacket);

        // The host's own player's requests, handled in-process (NetworkServer.LocalPlayer.cs).
        RegisterLocalHandlers();
    }

    private const float ProjectionRetryDelaySeconds = 5f;

    //allow mods to register their own packets
    public void RegisterExternalPacket<T>(ServerPacketHandler<T> handler) where T : class, IPacket, new()
    {
        netPacketProcessor.SubscribeReusable<T, ITransportPeer>((packet, peer) =>
        {
            var serverPlayer = TryGetServerPlayer(peer, out var player) ? GetWrapper(player) : null;
            handler(packet, serverPlayer);
        });
    }

    public void RegisterExternalSerializablePacket<T>(ServerPacketHandler<T> handler) where T : class, ISerializablePacket, new()
    {
        netPacketProcessor.SubscribeNetSerializable<ExternalSerializablePacketWrapper<T>, ITransportPeer>((wrapper, peer) =>
        {
            var serverPlayer = TryGetServerPlayer(peer, out var player) ? new ServerPlayerWrapper(player) : null;
            handler(wrapper.Packet, serverPlayer);
        },
        () => new ExternalSerializablePacketWrapper<T>()
        );
    }

    private void SendGameData(ServerPlayer player, ITransportPeer peer)
    {
        var gameParamsPacket = ClientboundGameParamsPacket.FromGameParams(Globals.G.GameParams);
        gameParamsPacket.FastTravelAdvancesTime = fastTravelAdvancesTime;

        SendPacket(peer, gameParamsPacket, DeliveryMethod.ReliableOrdered);
        SendPacket(peer, SaveGameDataPacketBuilder.Create(player), DeliveryMethod.ReliableOrdered);
    }

    private void OnLoaded()
    {
        // A dedicated host loads its world around a player rather than its parked body.
        DedicatedHostAnchor.Create();
        // Any multiplayer server keeps ground under its derailed cars, wherever they are.
        ServerWorldCoverage.Create();
        DedicatedHostSaveLock.Take();
        // ... and with nobody online yet, waits paused.
        DedicatedHost.OnPlayersChanged(someoneLeft: false);

        if (!IsSinglePlayer)
        {
            lobbyServerManager = NetworkLifecycle.Instance.GetOrAddComponent<LobbyServerManager>();
        }

        Log($"Track graph: {TrackGraph.Compute()}");
        Log($"Server loaded, processing {joinQueue.Count} queued players");
        IsLoaded = true;

        //We should initialise object here for dedicated servers, rather than relying on the existance of a client
        InitialiseStations();

        while (gameDataQueue.Count > 0)
        {
            ITransportPeer peer = gameDataQueue.Dequeue();
            if (peer.ConnectionState.Equals(TransportConnectionState.Connected) && TryGetServerPlayer(peer, out ServerPlayer waiting))
                SendGameData(waiting, peer);
        }

        while (joinQueue.Count > 0)
        {
            ITransportPeer peer = joinQueue.Dequeue();

            if (peer.ConnectionState.Equals(TransportConnectionState.Connected))
                OnServerboundLoadStateUpdatePacket(new ServerboundLoadStateUpdatePacket { LoadState = PlayerLoadingState.ReadyForWorldState }, peer);
            else
                LogDebug(() => $"Dropped queued join from peer {peer.Id}: no longer connected");
        }

        // A dedicated host has no player to finish loading: it leaves the
        // loading screen itself (a player host's own player does, in CompleteLoading).
        if (LocalPlayer == null)
        {
            Log("Dedicated host loaded");
            LoadingScreen.Finish();
        }

        lastTick = NetworkLifecycle.Instance.Tick;
        NetworkLifecycle.Instance.OnTick += OnTick;
    }

    private void OnTick(uint tick)
    {
        if (!IsLoaded)
            return;

        HazmatServer.Tick(tick);

        if ((NetworkLifecycle.Instance.Tick - lastTick) > NetworkLifecycle.TICK_RATE * WEATHER_UPDATE_INTERVAL)
        {
            SendWeatherState();
            lastTick = NetworkLifecycle.Instance.Tick;
        }

        foreach (PendingItemRecall pending in pendingItemRecalls.Values.ToArray())
            if (unchecked((int)(tick - pending.DeadlineTick)) >= 0)
                CancelPendingItemRecall(pending, "recall-prepare-timeout", true);

        if (trainTopologyCommitPending)
        {
            trainTopologyCommitPending = false;
            ushort[] controlCars = pendingTrainTopologyControlCars.ToArray();
            pendingTrainTopologyControlCars.Clear();
            ClientboundTrainTopologySnapshotPacket snapshot =
                trainTopologyAuthority.CommitObserved(controlCars: controlCars);
            if (snapshot.TopologyRevision == uint.MaxValue ||
                snapshot.TopologyRevision != lastBroadcastTrainTopologyRevision ||
                controlCars.Length > 0)
            {
                AttachUncoupleCauses(snapshot);
                SendPacketToAll(snapshot, DeliveryMethod.ReliableOrdered,
                    PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
                lastBroadcastTrainTopologyRevision = snapshot.TopologyRevision;
                DebugRuntime.Publish("train", "train.topology-snapshot-committed",
                    DebugRuntimeSide.Server, entityType: "Topology",
                    entityId: snapshot.TopologyRevision.ToString(),
                    data: DebugValueSnapshotter.SnapshotObject(new
                    {
                        snapshot.TopologyRevision,
                        endpointCount = snapshot.Endpoints?.Length ?? 0,
                        brakeLineCount = snapshot.BrakeLines?.Length ?? 0,
                        affectedControlCount = snapshot.ControlStates?.Length ?? 0
                    }));
            }
        }
    }

    public bool TryGetServerPlayer(ITransportPeer peer, out ServerPlayer player)
    {
        return peerToPlayer.TryGetValue(peer, out player);
    }

    public bool TryGetServerPlayer(byte playerId, out ServerPlayer player)
    {
        return serverPlayers.TryGetValue(playerId, out player);
    }

    public ServerPlayerWrapper GetWrapper(ServerPlayer serverPlayer)
    {
        if (!PlayerWrapperCache.TryGetValue(serverPlayer.PlayerId, out var wrapper))
        {
            wrapper = new ServerPlayerWrapper(serverPlayer);
            PlayerWrapperCache[serverPlayer.PlayerId] = wrapper;
        }
        return wrapper;
    }


    public override void OnPeerConnected(ITransportPeer peer)
    {
        LogDebug(() => $"OnPeerConnected({peer.Id})");
    }

    public override void OnPeerDisconnected(ITransportPeer peer, DisconnectReason disconnectReason)
    {
        LogDebug(() => $"OnPeerDisconnected({peer.Id})");
        if (!peerToPlayer.TryGetValue(peer, out ServerPlayer player) || player == null)
        {
            LogWarning($"Peer {peer.GetType()}, peerId: {peer.Id} disconnected but no player found");
            peerToPlayer.Remove(peer);
            return;
        }
        Log($"Player {player.Username} disconnected: {disconnectReason}");

        RunDisconnectStep("item recalls", () =>
        {
            foreach (PendingItemRecall pending in pendingItemRecalls.Values
                         .Where(value => value.Player == player).ToArray())
                CancelPendingItemRecall(pending, "requesting-player-disconnected", false);
        });
        RunDisconnectStep("spatial items", () => ServerItemManager.Instance?.OnSpatialPlayerDisconnected(player.PlayerId));
        RunDisconnectStep("held items", () => ServerItemManager.Instance?.DropHeldItemsOfDepartingPlayer(player));
        RunDisconnectStep("train controls", () =>
        {
            trainControlEnvelope.Forget(player.PlayerId);
        });

        if (WorldStreamingInit.isLoaded)
            RunDisconnectStep("save data", () => SaveGameManager.Instance.UpdateInternalData());

        // Every step above is guarded, so this always runs: no ghost player left behind.
        // (The save above still needs the departing player, for their position and inventory.)
        serverPlayers.Remove(player.PlayerId);
        peers.Remove(player.PlayerId);
        peerToPlayer.Remove(peer);

        SendPacketToAll
        (
            new ClientboundPlayerDisconnectPacket
            {
                PlayerId = player.PlayerId
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.Complete,
            excludeSelf: true
        );
        RunDisconnectStep("avatar", () => NetworkLifecycle.Instance.Avatars?.RemovePlayer(player.PlayerId));

        // One subscriber throwing must not skip the others (cars release control leases here).
        if (PlayerDisconnected != null)
            foreach (Action<ServerPlayer> handler in PlayerDisconnected.GetInvocationList())
                RunDisconnectStep(handler.Method.DeclaringType?.Name ?? "subscriber", () => handler(player));

        RunDisconnectStep("authentication", () => authentication.OnDisconnected(player));
        player.Dispose();
        if (player == LocalPlayer)
            LocalPlayer = null;
        RunDisconnectStep("dedicated host", () => DedicatedHost.OnPlayersChanged(someoneLeft: true));
    }

    private void RunDisconnectStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            LogError($"Player disconnect cleanup ({step}) threw: {e}");
        }
    }

    public override void OnNetworkLatencyUpdate(ITransportPeer peer, int latency)
    {
        DebugDiagnostics.RecordLatency(DebugRuntimeSide.Server, peer?.Id ?? -1, latency);
        if (!TryGetServerPlayer(peer, out var player))
            return;

        ClientboundPingUpdatePacket clientboundPingUpdatePacket = new()
        {
            PlayerId = player.PlayerId,
            Ping = latency
        };

        SendPacketToAll(clientboundPingUpdatePacket, DeliveryMethod.ReliableUnordered, PlayerLoadingState.None, peer,
            excludeSelf: true);
        NetworkLifecycle.Instance.Avatars?.UpdatePing(player.PlayerId, latency);

        if (latency > LATENCY_FLAG)
        {
            if ((NetworkLifecycle.Instance.Tick - player.LastHighPingTickLogged) > NetworkLifecycle.TICK_RATE * HIGH_PING_LOG_INTERVAL)
            {
                LogWarning($"High Ping Detected! Player: \"{player.Username}\", ping: {latency}ms");
                player.LastHighPingTickLogged = NetworkLifecycle.Instance.Tick;
            }
        }

        // Ensure we don't send a TickSync packet to ourselves
        if (peer == SelfPeer)
            return;

        SendPacket(peer, new ClientboundTickSyncPacket
        {
            ServerTick = NetworkLifecycle.Instance.Tick
        }, DeliveryMethod.ReliableUnordered);
    }

    public override void OnConnectionRequest(NetDataReader requestData, IConnectionRequest request)
    {
        LogDebug(() => $"NetworkServer OnConnectionRequest");
        netPacketProcessor.ReadAllPackets(requestData, request);
    }



    private void SendPacketToAll<T>(T packet, DeliveryMethod deliveryMethod, PlayerLoadingState minimumLoadState, bool excludeSelf = false) where T : class, new()
    {
        NetDataWriter writer = WritePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (excludeSelf && peer == SelfPeer)
                continue;

            if (TryGetServerPlayer(peer, out var player) && player.LoadingState < minimumLoadState)
            {
                HoldForTrainBaseline(player, writer, deliveryMethod, minimumLoadState);
                continue;
            }

            TraceItemDeliveryExpected(packet, peer, deliveryMethod);
            DebugTrace.PacketSending(peer, writer, deliveryMethod, DebugRuntimeSide.Server, typeof(T).Name);
            peer?.Send(writer, deliveryMethod);
        }
    }

    private void SendPacketToAll<T>(T packet, DeliveryMethod deliveryMethod, PlayerLoadingState minimumLoadState, ITransportPeer excludePeer, bool excludeSelf = false) where T : class, new()
    {
        NetDataWriter writer = WritePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (peer == excludePeer || (excludeSelf && peer == SelfPeer))
                continue;

            if (TryGetServerPlayer(peer, out var player) && player.LoadingState < minimumLoadState)
            {
                HoldForTrainBaseline(player, writer, deliveryMethod, minimumLoadState);
                continue;
            }

            TraceItemDeliveryExpected(packet, peer, deliveryMethod);
            DebugTrace.PacketSending(peer, writer, deliveryMethod, DebugRuntimeSide.Server, typeof(T).Name);
            peer?.Send(writer, deliveryMethod);
        }
    }

    // Rolling-stock changes broadcast between a joining player's train baseline and their
    // acknowledgement of it used to skip them entirely (they weren't ReadyForTrainSets yet), so a
    // car spawned, deleted or coupled in that window never reached them. Hold the reliable ones.
    private const int MaxHeldTrainPackets = 20000;

    private void SendNetSerializablePacketToAll<T>(T packet, DeliveryMethod deliveryMethod, bool excludeSelf = false) where T : INetSerializable, new()
    {
        NetDataWriter writer = WriteNetSerializablePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (excludeSelf && peer == SelfPeer)
                continue;

            DebugTrace.PacketSending(peer, writer, deliveryMethod, DebugRuntimeSide.Server, typeof(T).Name);
            peer?.Send(writer, deliveryMethod);
        }
    }

    private void SendNetSerializablePacketToAll<T>(T packet, DeliveryMethod deliveryMethod, ITransportPeer excludePeer, bool excludeSelf = false) where T : INetSerializable, new()
    {
        NetDataWriter writer = WriteNetSerializablePacket(packet);
        foreach (var peer in peers.Values)
        {
            if (peer == excludePeer || (excludeSelf && peer == SelfPeer))
                continue;
            DebugTrace.PacketSending(peer, writer, deliveryMethod, DebugRuntimeSide.Server, typeof(T).Name);
            peer?.Send(writer, deliveryMethod);
        }
    }

    public void SendExternalPacketToAll<T>(T packet, bool reliable, bool excludeSelf = false) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        SendPacketToAll(packet, deliveryMethod, PlayerLoadingState.None, excludeSelf);
    }

    public void SendExternalPacketToAll<T>(T packet, bool reliable, ITransportPeer excludePeer, bool excludeSelf = false) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;

        if (excludePeer == null)
            SendPacketToAll(packet, deliveryMethod, PlayerLoadingState.None, excludeSelf);
        else
            SendPacketToAll(packet, deliveryMethod, PlayerLoadingState.None, excludePeer, excludeSelf);
    }

    public void SendExternalSerializablePacketToAll<T>(T packet, bool reliable, bool excludeSelf = false) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };
        SendNetSerializablePacketToAll(wrapper, deliveryMethod, excludeSelf);
    }

    public void SendExternalSerializablePacketToAll<T>(T packet, bool reliable, ITransportPeer excludePeer, bool excludeSelf = false) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };

        if (excludePeer == null)
            SendNetSerializablePacketToAll(wrapper, deliveryMethod, excludeSelf);
        else
            SendNetSerializablePacketToAll(wrapper, deliveryMethod, excludePeer, excludeSelf);
    }

    public void SendExternalPacketToPlayer<T>(T packet, ITransportPeer peer, bool reliable) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        SendPacket(peer, packet, deliveryMethod);
    }

    public void SendExternalSerializablePacketToPlayer<T>(T packet, ITransportPeer peer, bool reliable) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };

        SendNetSerializablePacket(peer, wrapper, deliveryMethod);
    }


    public void SendRpcResponse(uint ticketId, IRpcResponse response, ITransportPeer peer)
    {
        SendNetSerializablePacket
        (
            peer,
            new ClientboundRpcResponsePacket
            {
                TicketId = ticketId,
                Response = response
            },
            DeliveryMethod.ReliableOrdered
        );
    }

    public void KickPlayer(ServerPlayer player, string reason = null)
    {
        if (player == null || player.Peer == null)
            return;

        player.Peer.Disconnect(WritePacket(new ClientboundDisconnectPacket { Kicked = true, Reason = reason ?? string.Empty }));
    }

    private bool CheckTrackGraph(ServerPlayer player)
    {
        TrackGraphFingerprint hostGraph = TrackGraph.Compute();
        string mismatch = hostGraph.DescribeMismatch(player.TrackGraph);
        if (mismatch == null)
        {
            Log($"Player {player.Username}'s track graph matches: {hostGraph}");
            return true;
        }

        LogError($"Refusing {player.Username}: track graph {player.TrackGraph} doesn't match the host's {hostGraph}");
        KickPlayer(player, mismatch);
        return false;
    }

    // Why couplings came apart since the last broadcast topology snapshot, sent with the next one.
    private const int MaxPendingUncoupleCauses = 256;
    private readonly List<TrainUncoupleCauseData> pendingUncoupleCauses = new();

    public void SendPlayerPreferencesUpdate(ServerPlayer player, Dictionary<PlayerPreference, string> preferences)
    {
        Log($"Sending player preferences update for '{player.Username}'");

        var packet = new ClientboundPlayerPreferencesUpdatePacket
        {
            PlayerId = player.PlayerId,
            PreferenceKeys = Array.ConvertAll(preferences.Keys.ToArray(), item => (byte)item),
            PreferenceValues = preferences.Values.ToArray()
        };

        // To the sender too: a player's own crew name is how it learns it.
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.Complete);
        if (player != LocalPlayer)
            NetworkLifecycle.Instance.Avatars?.UpdatePreferences(player.PlayerId, preferences);
    }

    private static Dictionary<string, object> Merge(Dictionary<string, object> target, Dictionary<string, object> additions)
    {
        foreach (var pair in additions) target[pair.Key] = pair.Value;
        return target;
    }

    public void SendChat(string message, ServerPlayer exclude = null)
    {
        var packet = new CommonChatPacket
        {
            message = message
        };

        if (exclude != null)
            SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.Complete, exclude.Peer);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.Complete);
    }

    public void SendWhisper(string message, ServerPlayer recipient)
    {
        if (!string.IsNullOrEmpty(message) && recipient != null && recipient.Peer != null)
        {
            NetworkServer.Instance.SendPacket
            (
                recipient.Peer,
                new CommonChatPacket
                {
                    message = message
                },
                DeliveryMethod.ReliableUnordered
            );
        }
    }



    private void OnServerboundClientLoginPacket(ServerboundClientLoginPacket packet, IConnectionRequest request)
    {
        Log($"Received login request from {packet.Username}{(Multiplayer.Settings.LogIps ? $" at {request.RemoteEndPoint.Address}" : "")}");

        LogDebug(() => $"OnServerboundClientLoginPacket from {packet.Username}");

        // clean up username - remove leading/trailing white space, swap spaces for underscores and truncate
        packet.Username = packet.Username.Trim().Replace(' ', '_').Truncate(Settings.MAX_USERNAME_LENGTH);
        string overrideUsername = packet.Username;

        Guid guid;
        try
        {
            guid = new Guid(packet.Guid);
        }
        catch (ArgumentException)
        {
            // This can only happen if the sent GUID is tampered with, in which case, we aren't worried about showing a message.
            Log($"Invalid GUID from {packet.Username}{(Multiplayer.Settings.LogIps ? $" at {request.RemoteEndPoint.Address}" : "")}");
            request.Reject();
            return;
        }

        Log($"Processing login packet for {packet.Username}{(Multiplayer.Settings.LogIps ? $" at {request.RemoteEndPoint.Address}" : "")}");

        if (Multiplayer.Settings.Password != packet.Password)
        {
            LogWarning("Denied login due to invalid password!");
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__INVALID_PASSWORD_KEY
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        if (packet.BuildVersion != FrontEnd.BuildVersion)
        {
            LogWarning($"Denied login to incorrect game version! Got: {packet.BuildVersion}, expected: {FrontEnd.BuildVersion}");
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__GAME_VERSION_KEY,
                ReasonArgs = [FrontEnd.BuildVersion, packet.BuildVersion.ToString()]
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        var validation = ModCompatibilityManager.Instance.ValidateClientMods(packet.Mods);
        if (!validation.IsValid)
        {
            RejectForMods(packet, validation, request);
            return;
        }

        if (!authentication.TryResolve(packet, guid, out PlayerAuthentication.Identity identity))
        {
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__AUTH_FAILED_KEY
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }
        Guid playerId = identity.PlayerId;
        Log($"{packet.Username} is player {PlayerIdentity.Describe(playerId)}");

        // The same player reconnecting (e.g. after a crash) before their old connection has timed
        // out. Retire the stale session now: left alone it holds their slot, and when it finally
        // times out its cleanup unbinds item ownership by identity - including from the new session.
        ServerPlayer stale = ServerPlayers.FirstOrDefault(player =>
            playerId != Guid.Empty && player.Guid == playerId && player.Peer != SelfPeer);
        if (stale != null)
        {
            Log($"{packet.Username} reconnected while their previous session (player {stale.PlayerId}) was still open; closing it");
            ITransportPeer stalePeer = stale.Peer;
            OnPeerDisconnected(stalePeer, DisconnectReason.Reconnect);
            stalePeer?.Disconnect();
        }

        // ensure the username is unique
        int uniqueName = ServerPlayers.Where(player => player.OriginalUsername.ToLower() == packet.Username.ToLower()).Count();
        if (uniqueName > 0)
            overrideUsername += uniqueName;

        if (PlayerCount >= Multiplayer.Settings.MaxPlayers || IsSinglePlayer && PlayerCount >= 1)
        {
            LogWarning("Denied login due to server being full!");
            authentication.Abandon(identity);
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__FULL_SERVER_KEY
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        // Unpause physics
        if (AppUtil.Instance.IsTimePaused)
            AppUtil.Instance.RequestSystemOnValueChanged(0.0f);

        ITransportPeer peer = request.Accept();

        ServerPlayer serverPlayer = AdmitPlayer(peer, overrideUsername, packet.Username, playerId, guid,
            identity.SteamId, packet.CharacterId, packet.IsVR, isLocalPlayer: false);
        authentication.Admit(identity, serverPlayer);

        ClientboundLoginResponsePacket acceptPacket = new()
        {
            Accepted = true,
            PlayerId = serverPlayer.PlayerId,
            OverrideUsername = serverPlayer.OriginalUsername == serverPlayer.Username ? string.Empty : overrideUsername,
            ProtocolFingerprint = ProtocolManifestProvider.Current.Fingerprint,
        };

        SendPacket(peer, acceptPacket, DeliveryMethod.ReliableUnordered);
    }

    /// <summary>
    ///     Admits a player whose login has been accepted: creates their <see cref="ServerPlayer"/>
    ///     and binds their saved records to it (their ID, items, Lost and Found and world items), as
    ///     every login must, the host's own player's included (host-player-persist). The local
    ///     player becomes <see cref="LocalPlayer"/> before the records are bound.
    /// </summary>
    private ServerPlayer AdmitPlayer(ITransportPeer peer, string username, string originalUsername, Guid playerId,
        Guid legacyGuid, ulong steamId, string characterId, bool isVr, bool isLocalPlayer)
    {
        ServerPlayer serverPlayer = new(peer, username, originalUsername, playerId, characterId, isVr)
        {
            LegacyGuid = legacyGuid,
            SteamId = steamId,
        };

        serverPlayers.Add(serverPlayer.PlayerId, serverPlayer);
        peerToPlayer.Add(peer, serverPlayer);
        if (isLocalPlayer)
            LocalPlayer = serverPlayer;
        global::Multiplayer.Server.Career.InstallGuidRekey.OnLogin(serverPlayer);
        AuthoritativeItemRegistry.RebindPersistentOwner(serverPlayer);
        NetworkedLostAndFoundManager.OnOwnerConnected(serverPlayer);
        WorldItemPersistenceManager.OnOwnerConnected(serverPlayer);
        DedicatedHost.OnPlayersChanged(someoneLeft: false);
        return serverPlayer;
    }

    private void RejectForMods(ServerboundClientLoginPacket packet, ModValidationResult validation, IConnectionRequest request)
    {
        LogWarning($"Denied login due to mod mismatch! {validation.Missing.Count} missing, {validation.Extra} extra");
        LogDebug(() =>
        {
            StringBuilder sb = new("Mod mis-match:");
            sb.AppendLine("Server Mods:");
            foreach (ModInfo mod in ModCompatibilityManager.Instance.GetLocalMods())
                sb.AppendLine($"\t{mod.Id} {mod.Version}, Status: {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

            sb.AppendLine("\r\nClient Mods:");
            foreach (ModInfo mod in packet.Mods)
                sb.AppendLine($"\t{mod.Id} {mod.Version}, Status (if known): {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

            sb.AppendLine("\r\nMissing Mods:");
            foreach (ModInfo mod in validation.Missing)
                sb.AppendLine($"\t{mod.Id} {mod.Version}, Status: {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

            sb.AppendLine("\r\nExtra Mods:");
            foreach (ModInfo mod in validation.Extra)
                sb.AppendLine($"\t{mod.Id} {mod.Version}, Status (if known): {ModCompatibilityManager.Instance.GetCompatibility(mod)}");

            return sb.ToString();
        });

        ClientboundLoginResponsePacket denyPacket = new()
        {
            ReasonKey = Locale.DISCONN_REASON__MODS_KEY,
            Missing = validation.Missing.ToArray(),
            Extra = validation.Extra.ToArray(),
        };
        request.Reject(WritePacket(denyPacket));
    }

    private void OnServerboundLoadStateUpdatePacket(ServerboundLoadStateUpdatePacket packet, ITransportPeer peer)
    {
        LogDebug(() => $"OnServerboundLoadStateUpdatePacket from peerId: {peer.Id}, loadState: {packet.LoadState}");
        if (peer is LiteNetLibPeer)
            Log($"Debug loopback peer {peer.Id} requested load state {packet.LoadState}");
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogError($"Load state update received for {peer.GetType()}, peerId: {peer.Id}, but ServerPlayer not found");
            peer.Disconnect();
            return;
        }

        if (player.LoadingState >= packet.LoadState)
        {
            LogWarning($"Player {player.Username} reported load state {packet.LoadState}, but is currently at load state {player.LoadingState}!");
            KickPlayer(player);
            return;
        }

        // Moving past ReadyForTrainSets acknowledges the train baseline: deliver what was held,
        // in order, before anything this state sends.
        if (packet.LoadState > PlayerLoadingState.ReadyForTrainSets)
            FlushHeldTrainTraffic(player);

        bool advanceLoadingState = true;
        switch (packet.LoadState)
        {
            case PlayerLoadingState.None:
                LogWarning($"Player {player.Username} sent unexpected state: {packet.LoadState}");

                break;

            case PlayerLoadingState.ReadyForGameData:
                Log($"Player {player.Username} is ready for game data");

                PlayerConnected?.Invoke(player);

                // Until the host's save has loaded, its difficulty is still the defaults (map markers, fast
                // travel and the rest all allowed). A player joining then kept them for the session.
                if (!IsLoaded)
                {
                    Log($"Player {player.Username} wants game data but the server is still loading, holding it");
                    gameDataQueue.Enqueue(peer);
                    break;
                }
                SendGameData(player, peer);

                break;

            case PlayerLoadingState.ReadyForWorldState:
                // The join queue replays this state with an empty packet: keep the first report.
                if (packet.TrackGraphHash != 0)
                    player.TrackGraph = new TrackGraphFingerprint(packet.TrackGraphHash, packet.TrackCount, packet.JunctionCount, packet.TurntableCount);

                if (!IsLoaded)
                {
                    Log($"Player {player.Username} is ready but server is still loading, adding to the queue");

                    joinQueue.Enqueue(peer);
                    SendPacket(peer, new ClientboundServerLoadingPacket(), DeliveryMethod.ReliableOrdered);

                    return;
                }
                else
                {
                    Log($"Player {player.Username} is ready for world state");
                }

                // Tracks, junctions and turntables are numbered as they wake in each game, so a
                // client whose railway differs would put every car on the wrong track. Refuse it
                // before it gets any world state or train baseline.
                if (!NetworkLifecycle.Instance.IsHost(player) && !CheckTrackGraph(player))
                    return;

                // Unpause physics
                if (AppUtil.Instance.IsTimePaused)
                    AppUtil.Instance.RequestSystemOnValueChanged(0.0f);

                // Allow the player to receive packets
                peers.Add(player.PlayerId, peer);

                if (NetworkLifecycle.Instance.IsHost(player))
                {
                    Log($"Server loaded. Triggering loading screen removal");
                    packet.LoadState = PlayerLoadingState.Complete;
                    break;
                }

                // Send weather state
                SendWeatherState(peer);
                SendSleepState(peer);
                SendShopStock(peer);

                // Send junctions and turntables
                SendPacket(peer, new ClientboundRailwayStatePacket
                {
                    SelectedJunctionBranches = NetworkedJunction.IndexedJunctions.Select(j => j.Junction.selectedBranch).ToArray(),
                    TurntableRotations = NetworkedTurntable.IndexedTurntables.Select(j => j.TurntableRailTrack.currentYRotation).ToArray()
                }, DeliveryMethod.ReliableOrdered);

                // Send generic switch states
                foreach (var genericSwitch in NetworkedGenericSwitch.AllSwitches)
                {
                    SendPacket(peer, new CommonGenericSwitchStatePacket
                    {
                        NetId = genericSwitch.NetId,
                        IsOn = genericSwitch.IsOn
                    }, DeliveryMethod.ReliableOrdered);
                }

                // Send customisations (holes drilled in world space)
                SendPacket(peer, new ClientboundCustomizationsPacket
                {
                    Holes = CustomizationHoleData.FromHoles(WorldCustomization.I.Holes)
                }, DeliveryMethod.ReliableOrdered);

                break;

            case PlayerLoadingState.ReadyForTrainSets:
                // Freeze and serialize one authoritative snapshot before announcing its size. A
                // trainset that cannot be represented rejects the whole baseline.
                List<ClientboundSpawnTrainSetPacket> baselinePackets = new();
                List<string> baselineFailures = new();
                foreach (Trainset set in Trainset.allSets.ToArray())
                {
                    // A trainset with no live cars has nothing to send. DV can leave one behind
                    // when its own split throws (two cars coupled at both ends, which only cars
                    // spawned inside each other manage); it used to make every join fail.
                    if (set == null || set.cars == null || !set.cars.Any(car => car != null))
                    {
                        LogWarning($"Skipping an empty trainset ({set?.id}) in the rolling-stock baseline");
                        continue;
                    }
                    // Nor does one whose cars are all deleted (switched off and no longer
                    // networked), which a car deleted twice used to leave behind (#84).
                    if (!set.cars.Any(car => car != null && car.gameObject.activeInHierarchy && car.TryNetworked(out _)))
                    {
                        LogWarning($"Skipping trainset {set.id} in the rolling-stock baseline: its cars ({string.Join(", ", set.cars.Where(car => car != null).Select(car => car.ID))}) are deleted");
                        continue;
                    }
                    try
                    {
                        ClientboundSpawnTrainSetPacket spawnPacket =
                            ClientboundSpawnTrainSetPacket.FromTrainSet(set.cars.ToList(), false);
                        if (spawnPacket.SpawnParts == null || spawnPacket.SpawnParts.Length == 0)
                            throw new InvalidOperationException("trainset produced no spawn parts");
                        baselinePackets.Add(spawnPacket);
                    }
                    catch (Exception e)
                    {
                        string failure = $"trainset [{set?.firstCar?.ID}, {set?.firstCar?.GetNetId()}]: {e.Message}";
                        baselineFailures.Add(failure);
                        LogError($"Rolling-stock baseline rejected {failure}\n{e}");
                    }
                }

                uint baselineId = ++nextTrainBaselineId;
                if (baselineId == 0)
                    baselineId = ++nextTrainBaselineId;
                uint carCount = (uint)baselinePackets.Sum(value => value.SpawnParts.Length);

                if (baselineFailures.Count > 0)
                {
                    SendPacket(peer, new ClientboundTrainBaselineBeginPacket
                    {
                        BaselineId = baselineId,
                        TrainsetCount = (uint)baselinePackets.Count,
                        CarCount = carCount,
                        Failure = string.Join("; ", baselineFailures)
                    }, DeliveryMethod.ReliableOrdered);
                    advanceLoadingState = false;
                    break;
                }

                SendPacket(peer, new ClientboundTrainBaselineBeginPacket
                {
                    BaselineId = baselineId,
                    TrainsetCount = (uint)baselinePackets.Count,
                    CarCount = carCount,
                    Failure = string.Empty
                }, DeliveryMethod.ReliableOrdered);

                for (int index = 0; index < baselinePackets.Count; index++)
                {
                    baselinePackets[index].BaselineId = baselineId;
                    baselinePackets[index].Sequence = (uint)index;
                    SendPacket(peer, baselinePackets[index], DeliveryMethod.ReliableOrdered);
                }

                SendPacket(peer, trainTopologyAuthority.Snapshot(baselineId),
                    DeliveryMethod.ReliableOrdered);

                SendPacket(peer, new ClientboundTrainBaselineCompletePacket
                {
                    BaselineId = baselineId,
                    TrainsetCount = (uint)baselinePackets.Count,
                    CarCount = carCount
                }, DeliveryMethod.ReliableOrdered);

                // Live rolling-stock traffic remains gated until the client proves it applied the
                // baseline by advancing to ReadyForItems; meanwhile it is held, not dropped.
                advanceLoadingState = false;
                player.PendingTrainTraffic.Clear();
                player.TrainBaselinePending = true;

                break;

            case PlayerLoadingState.ReadyForItems:
                // Before the player's items go out: a save may hold two of their essentials on one
                // slot, which left one with no silhouette and no recall.
                AuthoritativeItemRegistry.EnsureDistinctClaimSlots(player, "owner-loading");
                // World items will be spawned when the player is within the culling range
                // Installed gadgets may be spawnable in the culling range but needs more thought as there are a lot of extra parameters
                var installedGadgets = StorageController.Instance.StorageInstalledGadgets.GetStorageItemList();
                break;

            case PlayerLoadingState.ReadyForJobs:

                // Send Job Data
                foreach (StationController station in StationController.allStations)
                {
                    if (NetworkedStationController.GetFromStationController(station, out NetworkedStationController netStation))
                    {
                        //only send active jobs (available or in progress) - new clients don't need to know about old jobs
                        NetworkedJob[] jobs = netStation.NetworkedJobs
                            .Where(j => j.Job.State == JobState.Available || j.Job.State == JobState.InProgress)
                            .ToArray();

                        for (int i = 0; i < jobs.Length; i++)
                        {
                            SendJobsCreatePacket(netStation, [jobs[i]], peer);
                        }
                    }
                    else
                    {
                        LogError($"Sending job packets... Failed to get NetworkedStation from station {station?.stationInfo?.Name}");
                    }
                }
                break;

            case PlayerLoadingState.ReadyForTiles:
                HazmatServer.SendAll(peer);
                global::Multiplayer.Server.Trains.CargoFireServer.SendAll(peer);
                break;

            case PlayerLoadingState.Complete:

                break;

            default:
                LogWarning($"Player {player.Username} sent unexpected load state: {packet.LoadState}");
                KickPlayer(player);

                break;
        }

        if (advanceLoadingState)
            player.LoadingState = packet.LoadState;

        if (packet.LoadState == PlayerLoadingState.Complete)
            CompleteLoading(player);
    }

    /// <summary>
    ///     A player has finished loading: the others are told and it's sent them, then its loading
    ///     screen comes down.
    /// </summary>
    private void CompleteLoading(ServerPlayer player)
    {
        ITransportPeer peer = player.Peer;
        Log($"Player {player.Username} has completed loading");

        // Send the new player to all other players
        ClientboundPlayerJoinedPacket clientboundPlayerJoinedPacket = new()
        {
            PlayerId = player.PlayerId,
            Username = player.Username,
            IsVR = player.IsVR,
            CharacterId = player.CharacterId,
            CrewName = player.CrewName,
            TrackingData = player.TrackingData,
            Posture = player.Posture,
            IsOnCar = player.CarId != 0,
            CarID = player.CarId,
        };

        SendPacketToAll(clientboundPlayerJoinedPacket, DeliveryMethod.ReliableOrdered, PlayerLoadingState.Complete, peer,
            excludeSelf: true);
        if (player != LocalPlayer)
        {
            NetworkLifecycle.Instance.Avatars?.AddPlayer(player.PlayerId, player.Username, player.CrewName, player.CharacterId, player.IsVR);
            NetworkLifecycle.Instance.Avatars?.UpdatePosition(player.PlayerId, player.TrackingData, player.Posture, player.CarId != 0, player.CarId);
        }

        // Announce player joined
        ChatManager.ServerMessage(player.Username + " joined the game", null, player);

        // Send existing players (the host's own player has their avatars already)
        foreach (ServerPlayer otherPlayer in ServerPlayers)
        {
            if (player.PlayerId == otherPlayer.PlayerId || player == LocalPlayer)
                continue;

            SendPacket(peer, new ClientboundPlayerJoinedPacket
            {
                PlayerId = otherPlayer.PlayerId,
                Username = otherPlayer.Username,
                CharacterId = otherPlayer.CharacterId,
                IsVR = otherPlayer.IsVR,
                CrewName = otherPlayer.CrewName,
                CarID = otherPlayer.CarId,
                TrackingData = otherPlayer.TrackingData,  // full merged state
                Posture = otherPlayer.Posture,
                IsOnCar = otherPlayer.CarId != 0,
            }, DeliveryMethod.ReliableOrdered);
        }

        SendPacket(peer, new ClientboundRemoveLoadingScreenPacket(), DeliveryMethod.ReliableOrdered);
        PlayerReady?.Invoke(player);
    }

    // The host's own player's preferences and chat are handled in-process (stage 2 of
    // docs/design/host-local-session.md); its position is read from its tracker
    // (UpdateLocalPlayerFromTracker).
    partial void RegisterLocalSession()
    {
        HandleLocal<ServerboundPlayerPreferenceUpdatePacket>(HandlePlayerPreferences);
        HandleLocal<CommonChatPacket>(HandleChat);
    }

    /// <summary>
    ///     Reads the host's own player from its tracker, merges it into its <see cref="ServerPlayer"/>
    ///     and relays it, as a position packet would: called before the host's requests are handled,
    ///     so their range checks see where the host's player is now. Nothing on a dedicated host,
    ///     which has no tracker.
    /// </summary>
    private void UpdateLocalPlayerFromTracker()
    {
        ILocalPlayerTracker tracker = LocalPlayerTracker.Current;
        ServerPlayer local = tracker == null || NetworkLifecycle.Instance.IsReturningToMenu ? null : LocalPlayer;
        if (local == null)
            return;

        try
        {
            ServerboundPlayerPositionPacket packet = tracker.Sample(out _);
            if (packet != null)
                HandlePlayerPosition(packet, local);
        }
        catch (Exception e)
        {
            LogError($"Reading the host's player from its tracker threw: {e}");
        }
    }

    private void OnServerboundPlayerPositionPacket(ServerboundPlayerPositionPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
        {
            HandlePlayerPosition(packet, player);
            return;
        }

        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Server, "Player", peer?.Id.ToString());
        DebugTrace.Validation("player", "Player", peer?.Id.ToString(), false, "unknown-player", DebugRuntimeSide.Server);
        LogWarning($"Received Player Position from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
    }

    private void HandlePlayerPosition(ServerboundPlayerPositionPacket packet, ServerPlayer player)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Server, "Player", player.Peer?.Id.ToString());

        // Merge incoming delta into stored state
        player.TrackingData = player.TrackingData.MergeFrom(packet.TrackingData);
        player.CarId = packet.CarID;
        player.Posture = packet.Posture;
        DebugRuntime.PublishHighFrequency("player", "player.tracking-applied", DebugRuntimeSide.Server, "Player", player.PlayerId.ToString(),
            () => DebugValueSnapshotter.SnapshotObject(new { packet.TrackingData, packet.CarID, packet.Posture, packet.IsOnCar }));

        SendPacketToAll(new ClientboundPlayerPositionPacket
        {
            PlayerId = player.PlayerId,
            TrackingData = packet.TrackingData,
            Posture = packet.Posture,
            IsOnCar = packet.IsOnCar,
            CarID = packet.CarID
        }, DeliveryMethod.Sequenced, PlayerLoadingState.Complete, player.Peer, excludeSelf: true);
        if (player != LocalPlayer)
            NetworkLifecycle.Instance.Avatars?.UpdatePosition(player.PlayerId, packet.TrackingData, packet.Posture, packet.IsOnCar, packet.CarID);
    }

    private void OnServerboundPlayerPreferenceUpdatePacket(ServerboundPlayerPreferenceUpdatePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"Received Player Preferences Update from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

        HandlePlayerPreferences(packet, player);
    }

    // The new preferences go to everyone, the sender too (for the host's player, presentation only).
    private void HandlePlayerPreferences(ServerboundPlayerPreferenceUpdatePacket packet, ServerPlayer player)
    {
        Dictionary<PlayerPreference, string> preferences = [];

        // Store the characterId for other players connecting to the server
        if (packet.GetPreferencesDictionary().TryGetValue(PlayerPreference.CharacterModel, out string characterId))
        {
            player.CharacterId = characterId;
            preferences.Add(PlayerPreference.CharacterModel, characterId);
        }

        if (preferences.Count > 0)
            SendPlayerPreferencesUpdate(player, preferences);
    }

    // The comms radio reaches about 100 m; allow for the car moving while the request travels.
    private const float MaxSandboxCargoDistance = 200f;

    // Couplers this far apart can't be joined (vanilla couples within 1.5 m); slack for latency.
    private const float MaxTopologyCouplerGap = 4f;
    // How far from a car a player can work its couplers, hoses and cocks; slack for latency.
    private const float MaxTopologyPlayerDistance = 8f;

    private void OnCommonChatPacket(CommonChatPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleChat(packet, player);
    }

    // A chat line or command, the host's player's too: the host is admin by IsHost(player).
    private void HandleChat(CommonChatPacket packet, ServerPlayer player) =>
        ChatManager.ProcessMessage(packet.message, player);

    private void OnUnconnectedPingPacket(UnconnectedPingPacket packet, IPEndPoint endPoint)
    {
        //Log($"OnUnconnectedPingPacket({endPoint.Address})");
        //SendUnconnectedPacket(packet, endPoint.Address.ToString(), endPoint.Port);
    }

    // Players given the first-join starter kit this session (ones with saved data never get it).
    internal readonly HashSet<Guid> StarterKitsGiven = new();

    // Well above any basket; refunds are further capped by what the player has paid in.
    private const double MaxShopPayment = 1_000_000;

    // Players buy within a few metres of the till; the margin covers walking off while it sends.
    private const float MaxShopPurchaseDistance = 50f;
    private const double ShopPriceTolerance = 0.5;

    private sealed class PendingItemRecall
    {
        public PendingItemRecall(uint operationId, ushort itemNetId, uint baseRevision,
            int requestedSlot, uint deadlineTick, ServerPlayer player, ITransportPeer peer)
        {
            OperationId = operationId;
            ItemNetId = itemNetId;
            BaseRevision = baseRevision;
            RequestedSlot = requestedSlot;
            DeadlineTick = deadlineTick;
            Player = player;
            Peer = peer;
        }

        public uint OperationId { get; }
        public ushort ItemNetId { get; }
        public uint BaseRevision { get; }
        public int RequestedSlot { get; }
        public uint DeadlineTick { get; }
        public ServerPlayer Player { get; }
        public ITransportPeer Peer { get; }
    }

    }
