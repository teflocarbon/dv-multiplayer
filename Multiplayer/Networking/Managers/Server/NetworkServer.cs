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
using Multiplayer.API;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Core.Containers;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Data.Player;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Data.World;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Clientbound.Jobs;
using Multiplayer.Networking.Packets.Clientbound.SaveGame;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Clientbound.World;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Networking.Packets.Common.Train;
using Multiplayer.Networking.Packets.Serverbound;
using Multiplayer.Networking.Packets.Serverbound.Jobs;
using Multiplayer.Networking.Packets.Serverbound.Train;
using Multiplayer.Networking.Packets.Unconnected;
using Multiplayer.Networking.Serialization;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Trains;
using Multiplayer.Patches.MainMenu;
using Multiplayer.Patches.World;
using Multiplayer.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using UnityEngine;

namespace Multiplayer.Networking.Managers.Server;

public class NetworkServer : NetworkManager
{
    private const int WEATHER_UPDATE_INTERVAL = 30; //seconds
    private const int HIGH_PING_LOG_INTERVAL = 60; //only log high ping once every 60 seconds per player

    public Action<ServerPlayer> PlayerConnected;
    public Action<ServerPlayer> PlayerDisconnected;
    public Action<ServerPlayer> PlayerReady;
    protected override string LogPrefix => "[Server]";

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

    public IReadOnlyCollection<ServerPlayer> ServerPlayers => serverPlayers.Values;
    public IReadOnlyCollection<ServerPlayerWrapper> ServerPlayerWrappers => PlayerWrapperCache.Values;
    public int PlayerCount => ServerPlayers.Count;

    private ITransportPeer _selfPeer;
    public ITransportPeer SelfPeer
    {
        get
        {
            if (_selfPeer != null)
                return _selfPeer;

            peers.TryGetValue(SelfId, out _selfPeer);
            return _selfPeer;
        }
    }

    public byte SelfId => NetworkLifecycle.Instance.Client?.PlayerId ?? 0;

    public readonly IDifficulty Difficulty;
    private bool IsLoaded;

    private readonly ChatManager _chatManager = new();
    public ChatManager ChatManager => _chatManager;

    private uint lastTick;
    private uint nextTrainBaselineId;
    private readonly TrainTopologyAuthority trainTopologyAuthority = new();
    private uint lastBroadcastTrainTopologyRevision;
    private bool trainTopologyCommitPending;
    private readonly HashSet<ushort> pendingTrainTopologyControlCars = new();
    private readonly Dictionary<(byte PlayerId, ushort CarNetId), ulong>
        lastTrainControlOperation = new();
    private uint nextItemRecallOperationId;
    private readonly Dictionary<uint, PendingItemRecall> pendingItemRecalls = new();
    private readonly Dictionary<ushort, uint> pendingItemRecallByItem = new();
    private const uint ItemRecallTimeoutTicks = NetworkLifecycle.TICK_RATE * 5;

    public NetworkServer(IDifficulty difficulty, Settings settings, bool singlePlayer, LobbyServerData serverData) : base(settings)
    {
        Log($"Server created for {(singlePlayer ? "single player" : "multiplayer")} game");
        HazmatSync.Reset();

        IsSinglePlayer = singlePlayer;
        ServerData = serverData;
        Difficulty = difficulty;

        fastTravelAdvancesTime = settings.FastTravelAdvancesTime;
        TimeAdvancePatch.FastTravelAdvancesTime = fastTravelAdvancesTime;
    }

    public override void OnSettingsUpdated(Settings settings)
    {
        base.OnSettingsUpdated(settings);

        if (settings.FastTravelAdvancesTime != fastTravelAdvancesTime)
        {
            fastTravelAdvancesTime = settings.FastTravelAdvancesTime;
            TimeAdvancePatch.FastTravelAdvancesTime = fastTravelAdvancesTime;
            SendGameParams(Globals.G.GameParams);
        }
    }

    public override bool Start(int port)
    {
        Log($"Starting server...");

        //setup paint theme lookup cache
        PaintThemeLookup.Instance.CheckInstance();

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
        netPacketProcessor.SubscribeReusable<ServerboundTrainSpawnRequestPacket, ITransportPeer>(OnServerboundTrainSpawnRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundWorkTrainRequestPacket, ITransportPeer>(OnServerboundWorkTrainRequestPacket);


        // Jobs
        netPacketProcessor.SubscribeReusable<ServerboundJobValidateRequestPacket, ITransportPeer>(OnServerboundJobValidateRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundJobBookletSummonRequestPacket, ITransportPeer>(OnServerboundJobBookletSummonRequestPacket);
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
    }

    private void OnServerboundItemSpatialSamplePacket(ServerboundItemSpatialSamplePacket packet,
        ITransportPeer peer)
    {
        if (packet?.State == null || !TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        NetworkedItemManager.Instance?.ReceiveItemSpatialSample(packet.State, player);
    }

    private void OnServerboundItemSpatialSettlementPacket(ServerboundItemSpatialSettlementPacket packet,
        ITransportPeer peer)
    {
        if (packet?.State == null || !TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        NetworkedItemManager.Instance?.ReceiveItemSpatialSettlement(packet.State, player);
    }

    private void OnServerboundItemTrainWakeWitnessPacket(
        ServerboundItemTrainWakeWitnessPacket packet, ITransportPeer peer)
    {
        if (packet == null || !TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        NetworkedItemManager.Instance?.ReceiveItemTrainWakeWitness(packet, player);
    }

    private void OnServerboundWorldItemCataloguePacket(ServerboundWorldItemCataloguePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        var host = NetworkedItemManager.Instance.GetAuthoredCatalogueSummary();
        bool accepted = packet != null && packet.CollisionCount == 0 && host.Collisions == 0 &&
                        packet.ItemCount == host.Count && string.Equals(packet.Digest, host.Digest, StringComparison.Ordinal);
        player.WorldItemCatalogueAccepted = accepted;
        string reason = accepted ? string.Empty :
            $"catalogue-mismatch:host={host.Count}/{host.Digest}/c{host.Collisions},client={packet?.ItemCount ?? 0}/{packet?.Digest ?? string.Empty}/c{packet?.CollisionCount ?? 0}";
        DebugRuntime.Publish("item-world", "world-item.catalogue-negotiated", DebugRuntimeSide.Server,
            accepted ? DebugSeverity.Info : DebugSeverity.Error, "Player", player.PlayerId.ToString(), new()
            {
                ["accepted"] = accepted, ["reason"] = reason,
                ["hostCount"] = host.Count, ["clientCount"] = packet?.ItemCount ?? 0
            });
        SendPacket(peer, new ClientboundWorldItemCataloguePacket
        {
            Accepted = accepted,
            HostItemCount = host.Count,
            HostCollisionCount = host.Collisions,
            HostDigest = host.Digest,
            Reason = reason
        }, DeliveryMethod.ReliableOrdered);
    }

    private const float ProjectionRetryDelaySeconds = 5f;

    private void OnServerboundWorldItemProjectionAckPacket(ServerboundWorldItemProjectionAckPacket packet, ITransportPeer peer)
    {
        if (packet == null || !TryGetServerPlayer(peer, out ServerPlayer player)) return;
        bool known = NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) && item != null;
        bool revisionMatches = known && AuthoritativeItemRegistry.TryGet(packet.ItemNetId, out var record) &&
                               record.Revision == packet.AuthorityRevision;
        if (packet.Projected && (!known || !revisionMatches || !player.KnownItems.ContainsKey(item)))
        {
            DebugRuntime.Publish("item-world", "world-item.projection-ack-rejected", DebugRuntimeSide.Server,
                DebugSeverity.Warning, "Item", packet.ItemNetId.ToString(), new()
                {
                    ["playerId"] = player.PlayerId,
                    ["knownItem"] = known,
                    ["revisionMatches"] = revisionMatches,
                    ["recipientExpectedItem"] = known && player.KnownItems.ContainsKey(item)
                });
            return;
        }
        if (packet.Projected) player.AcknowledgedWorldItems.Add(packet.ItemNetId);
        else player.AcknowledgedWorldItems.Remove(packet.ItemNetId);
        // A client that couldn't create an item we still consider relevant doesn't have it:
        // forget that it does, so the Create is sent again (after a pause, in case it keeps failing)
        // instead of a stream of updates for an object it never made.
        if (!packet.Projected && known && player.KnownItems.Remove(item))
            player.ProjectionRetryAt[packet.ItemNetId] = Time.time + ProjectionRetryDelaySeconds;
        DebugRuntime.Publish("item-world", packet.Projected ? "world-item.projection-enter-ack" : "world-item.projection-retire-ack",
            DebugRuntimeSide.Server, entityType: "Item", entityId: packet.ItemNetId.ToString(), data: new()
            {
                ["playerId"] = player.PlayerId,
                ["authorityRevision"] = packet.AuthorityRevision,
                ["authoredItemKey"] = known && AuthoritativeItemRegistry.TryGet(packet.ItemNetId,
                    out AuthoritativeItemRegistry.Record acknowledgedRecord)
                    ? acknowledgedRecord.AuthoredItemKey ?? string.Empty
                    : string.Empty
            });
    }

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
        SendPacket(peer, ClientboundSaveGameDataPacket.CreatePacket(player), DeliveryMethod.ReliableOrdered);
    }

    private void OnLoaded()
    {
        if (!IsSinglePlayer)
        {
            lobbyServerManager = NetworkLifecycle.Instance.GetOrAddComponent<LobbyServerManager>();
        }

        Log($"Server loaded, processing {joinQueue.Count} queued players");
        IsLoaded = true;

        //We should initialise object here for dedicated servers, rather than relying on the existance of a client
        NetworkedPitStopStation.InitialisePitStops();
        NetworkedCashRegisterWithModules.InitialiseCashRegisters();

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

        lastTick = NetworkLifecycle.Instance.Tick;
        NetworkLifecycle.Instance.OnTick += OnTick;
    }

    private void OnTick(uint tick)
    {
        if (!IsLoaded)
            return;

        HazmatSync.Server_Tick(tick);

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

    #region Net Events

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
        RunDisconnectStep("spatial items", () => NetworkedItemManager.Instance?.OnSpatialPlayerDisconnected(player.PlayerId));
        RunDisconnectStep("held items", () => NetworkedItemManager.Instance?.DropHeldItemsOfDepartingPlayer(player));
        RunDisconnectStep("train controls", () =>
        {
            foreach (var key in lastTrainControlOperation.Keys
                         .Where(value => value.PlayerId == player.PlayerId).ToArray())
                lastTrainControlOperation.Remove(key);
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
            PlayerLoadingState.Complete
        );

        // One subscriber throwing must not skip the others (cars release control leases here).
        if (PlayerDisconnected != null)
            foreach (Action<ServerPlayer> handler in PlayerDisconnected.GetInvocationList())
                RunDisconnectStep(handler.Method.DeclaringType?.Name ?? "subscriber", () => handler(player));

        player.Dispose();
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

        SendPacketToAll(clientboundPingUpdatePacket, DeliveryMethod.ReliableUnordered, PlayerLoadingState.None, peer);

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

    #endregion

    #region Packet Senders

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

    private void HoldForTrainBaseline(ServerPlayer player, NetDataWriter writer, DeliveryMethod deliveryMethod,
        PlayerLoadingState minimumLoadState)
    {
        if (!player.TrainBaselinePending || minimumLoadState != PlayerLoadingState.ReadyForTrainSets ||
            deliveryMethod is DeliveryMethod.Unreliable or DeliveryMethod.Sequenced)
            return;
        if (player.PendingTrainTraffic.Count >= MaxHeldTrainPackets)
        {
            if (player.PendingTrainTraffic.Count == MaxHeldTrainPackets)
                LogWarning($"Holding too much rolling-stock traffic for {player.Username}; dropping further packets");
            return;
        }
        player.PendingTrainTraffic.Add((writer.CopyData(), deliveryMethod));
    }

    private void FlushHeldTrainTraffic(ServerPlayer player)
    {
        if (!player.TrainBaselinePending)
            return;
        player.TrainBaselinePending = false;
        LogDebug(() => $"Delivering {player.PendingTrainTraffic.Count} rolling-stock packets held during {player.Username}'s baseline");
        foreach ((byte[] data, DeliveryMethod delivery) in player.PendingTrainTraffic)
            player.Peer?.Send(NetDataWriter.FromBytes(data, false), delivery);
        player.PendingTrainTraffic.Clear();
    }

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

    #region Mod Packets
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

    #endregion

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

    public void KickPlayer(ServerPlayer player)
    {
        if (player == null || player.Peer == null)
            return;

        player.Peer.Disconnect(WritePacket(new ClientboundDisconnectPacket { Kicked = true }));
    }

    public void SendGameParams(GameParams gameParams)
    {
        var packet = ClientboundGameParamsPacket.FromGameParams(gameParams);
        packet.FastTravelAdvancesTime = fastTravelAdvancesTime;
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForGameData, excludeSelf: true);
    }

    public void SendWeatherState(ITransportPeer peer = null)
    {
        var packet = WeatherDriver.Instance.GetSaveData(Globals.G.GameParams.WeatherEditorAlwaysAllowed).ToObject<ClientboundWeatherPacket>();

        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    public void SendSpawnTrainset(List<TrainCar> set, bool autoCouple, bool sendToAll, ITransportPeer sendTo = null)
    {

        LogDebug(() =>
        {
            StringBuilder sb = new();

            sb.Append($"SendSpawnTrainSet() Sending trainset {set?.FirstOrDefault()?.GetNetId()} with {set?.Count} cars");

            TrainCar[] noNetId = set?.Where(car => car.GetNetId() == 0).ToArray();

            if (noNetId.Length > 0)
                sb.AppendLine($"Erroneous cars!: {string.Join(", ", noNetId.Select(car => $"{{{car?.ID}, {car?.CarGUID}, {car.logicCar != null}}}"))}");

            return sb.ToString();

        });

        var packet = ClientboundSpawnTrainSetPacket.FromTrainSet(set, autoCouple);

        if (!sendToAll)
        {
            if (sendTo == null)
                LogError($"SendSpawnTrainSet() Trying to send to null peer!");
            else
                SendPacket(sendTo, packet, DeliveryMethod.ReliableOrdered);
        }
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);

        ClientboundTrainTopologySnapshotPacket topology = trainTopologyAuthority.CommitObserved();
        if (sendToAll)
        {
            SendPacketToAll(topology, DeliveryMethod.ReliableOrdered,
                PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
            lastBroadcastTrainTopologyRevision = topology.TopologyRevision;
        }
        else if (sendTo != null)
            SendPacket(sendTo, topology, DeliveryMethod.ReliableOrdered);
    }

    public void SendDestroyTrainCar(NetworkedTrainCar netTrainCar, ITransportPeer peer = null)
    {
        //ushort netID = trainCar.GetNetId();
        Log($"Sending DestroyTrainCarPacket for [{netTrainCar.CurrentID} {netTrainCar.NetId}]");

        if (netTrainCar.NetId == 0)
        {
            LogWarning($"SendDestroyTrainCar failed. [{netTrainCar.CurrentID} {netTrainCar.NetId}]");
            return;
        }

        var packet = new ClientboundDestroyTrainCarPacket { NetId = netTrainCar.NetId };

        if (peer == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
        else
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendTrainsetPhysicsUpdate(ClientboundTrainsetPhysicsPacket packet, bool reliable)
    {
        //LogDebug(() => $"Sending Physics packet for netId: {packet.FirstNetId}, tick: {packet.Tick}");
        SendPacketToAll(packet, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendBrakeState(ushort netId, float mainReservoirPressure, float brakePipePressure, float brakeCylinderPressure, float overheatPercent, float overheatReductionFactor, float temperature)
    {
        TrainTopologyNativeAdapter.TryCaptureBrakeLine(netId, out BrakeLineStateData line);
        SendPacketToAll(new ClientboundBrakeStateUpdatePacket
        {
            TopologyRevision = trainTopologyAuthority.Revision,
            NetId = netId,
            BrakeLineAnchorCarNetId = line?.AnchorCarNetId ?? netId,
            BrakeLineMemberDigest = line?.MemberDigest ?? 0,
            MainReservoirPressure = mainReservoirPressure,
            BrakePipePressure = line?.PipePressure ?? brakePipePressure,
            BrakeCylinderPressure = brakeCylinderPressure,
            OverheatPercent = overheatPercent,
            OverheatReductionFactor = overheatReductionFactor,
            Temperature = temperature
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);

        //LogDebug(()=> $"Sending Brake Pressures netId {netId}: {mainReservoirPressure}, {independentPipePressure}, {brakePipePressure}, {brakeCylinderPressure}");
    }

    public void SendExplosion(ushort carNetId, global::Multiplayer.Patches.Train.ExplosionSync.Kind kind, byte index)
    {
        SendPacketToAll(new ClientboundExplosionPacket { CarNetId = carNetId, Kind = (byte)kind, Index = index },
            DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
    }

    public void SendCargoState(NetworkedTrainCar netTraincar, bool isLoading, byte cargoModelIndex)
    {
        Car logicCar = netTraincar?.TrainCar?.logicCar;

        //LogDebug(() => $"SendCargoState({netTraincar?.CurrentID}, isLoading: {isLoading}, cargoModelIndex: {cargoModelIndex}), logicCar: {logicCar?.ID}, WareHouseMachineID: {logicCar.CargoOriginWarehouse?.ID}, warehouse track: {logicCar.CargoOriginWarehouse?.WarehouseTrack?.ID}");

        Log($"Sending Cargo State for {netTraincar?.CurrentID}, isLoading: {isLoading}, cargoModelIndex: {cargoModelIndex}");

        if (logicCar == null)
        {
            LogWarning($"Attempted to send cargo state for {netTraincar?.CurrentID}, but logic car does not exist!");
            return;
        }

        CargoType cargoTypeV1 = isLoading ? logicCar.CurrentCargoTypeInCar : logicCar.LastUnloadedCargoType;

        CargoTypeLookup.Instance.TryGetNetId(cargoTypeV1, out uint cargoType);

        ushort netMachineId = 0;
        if (logicCar.CargoOriginWarehouse != null)
        {
            if (!WarehouseMachineLookup.TryGetNetId(logicCar.CargoOriginWarehouse, out netMachineId))
            {
                Log($"Attempting to send cargo state for {netTraincar.CurrentID}, for warehouse machine at track {logicCar.CargoOriginWarehouse?.WarehouseTrack?.ID}, but Warehouse Machine was not found");
                return;
            }
        }

        SendPacketToAll(new ClientboundCargoStatePacket
        {
            NetId = netTraincar.NetId,
            IsLoading = isLoading,
            CargoTypeNetId = cargoType,
            CargoAmount = logicCar.LoadedCargoAmount,
            CargoHealth = netTraincar.TrainCar.CargoDamage.HealthPercentage,
            CargoModelIndex = cargoModelIndex,
            WarehouseMachineNetId = netMachineId,
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendPaintThemeChange(NetworkedTrainCar netTraincar, TrainCarPaint.Target targetArea, uint themeNetId, ServerPlayer sendToPlayer = null)
    {
        var packet = new CommonPaintThemePacket
        {
            NetId = netTraincar.NetId,
            TargetArea = targetArea,
            PaintThemeId = themeNetId
        };

        Log($"Sending paint theme change for {netTraincar.CurrentID}");

        if (sendToPlayer != null)
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableUnordered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForTrainSets, true);
    }

    public void SendRestorationStateChange(ushort netId, LocoRestorationController.RestorationState newState, ushort[] transportCars)
    {
        var packet = new ClientboundRestorationStateChangePacket
        {
            NetId = netId,
            NewState = newState,
            TransportCarNetIds = transportCars
        };

        Log($"Sending restoration state change for {netId}, new state: {newState}, transport cars count: {transportCars?.Count() ?? 0}");

        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForItems, true);
    }

    public void SendWarehouseControllerUpdate(ushort netId, bool isLoading, ushort jobNetId, ushort carNetId, uint cargoTypeNetId, WarehouseMachineController.TextPreset preset)
    {
        LogDebug(() => $"SendWarehouseControllerUpdate({netId}, {isLoading}, {jobNetId}, {carNetId}, {cargoTypeNetId}, {preset})");

        SendPacketToAll(new ClientboundWarehouseControllerUpdatePacket()
        {
            NetId = netId,
            IsLoading = isLoading,
            JobNetId = jobNetId,
            CarNetId = carNetId,
            CargoTypeNetId = cargoTypeNetId,
            Preset = (ushort)preset,
        },
        DeliveryMethod.Sequenced, PlayerLoadingState.ReadyForJobs, SelfPeer);
    }

    public void SendCargoHealthUpdate(ushort netId, float currentHealth)
    {
        SendPacketToAll(new ClientboundCargoHealthUpdatePacket
        {
            NetId = netId,
            CargoHealth = currentHealth,
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendCarHealthUpdate(ushort netId, TrainCarHealthData health)
    {

        //LogDebug(() => $"Sending Car Health Update for netId {netId}: BodyHP: {health.BodyHP}, WheelsHP: {health.WheelsHP}, MechanicalPT: {health.MechanicalPT}, ElectricalPT: {health.ElectricalPT}, WindowsBroken: {health.WindowsBroken}");

        SendPacketToAll(new ClientboundCarHealthUpdatePacket
        {
            NetId = netId,
            Health = health
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendRerailTrainCar(ushort netId, ushort rerailTrack, Vector3 worldPos, Vector3 forward)
    {
        SendPacketToAll(new ClientboundRerailTrainPacket
        {
            NetId = netId,
            TrackId = rerailTrack,
            Position = worldPos,
            Forward = forward
        }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, SelfPeer);
    }

    public void SendMoveTrainCarToTrack(ushort netId, ushort destinationTrack, Vector3 worldPos, Vector3 forward, bool isTeleporting)
    {
        LogDebug(() => $"SendMoveTrainCarToTrack({netId}, {destinationTrack}, {worldPos}, {forward}, {isTeleporting})");
        SendPacketToAll
        (
            new ClientboundMoveTrainPacket
            {
                NetId = netId,
                TrackId = destinationTrack,
                Position = worldPos,
                Forward = forward,
                IsTeleporting = isTeleporting
            }, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, true
        );
    }

    public void SendWindowsBroken(ushort netId, Vector3 forceDirection)
    {
        SendPacketToAll
        (
            new ClientboundWindowsBrokenPacket
            {
                NetId = netId,
                ForceDirection = forceDirection
            }, DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForTrainSets, SelfPeer
        );
    }

    public void SendWindowsRepaired(ushort netId)
    {
        SendPacketToAll
        (
            new ClientboundWindowsRepairedPacket
            {
                NetId = netId
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForTrainSets,
            excludeSelf: true
        );
    }

    public void SendMoney(float amount)
    {
        SendPacketToAll
        (
            new ClientboundMoneyPacket
            {
                Amount = amount
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendLicense(string id, bool isJobLicense)
    {
        SendPacketToAll
        (
            new ClientboundLicenseAcquiredPacket
            {
                Id = id,
                IsJobLicense = isJobLicense
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendGarage(string id)
    {
        SendPacketToAll
        (
            new ClientboundGarageUnlockPacket
            {
                Id = id
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            excludeSelf: true
        );
    }

    public void SendDebtStatus(bool hasDebt)
    {
        SendPacketToAll
        (
            new ClientboundDebtStatusPacket
            {
                HasDebt = hasDebt
            }, DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            SelfPeer
        );
    }

    public void SendPlayerPreferencesUpdate(ServerPlayer player, Dictionary<PlayerPreference, string> preferences)
    {
        Log($"Sending player preferences update for '{player.Username}'");

        var packet = new ClientboundPlayerPreferencesUpdatePacket
        {
            PlayerId = player.PlayerId,
            PreferenceKeys = Array.ConvertAll(preferences.Keys.ToArray(), item => (byte)item),
            PreferenceValues = preferences.Values.ToArray()
        };

        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, PlayerLoadingState.Complete);
    }

    public void SendTrainUncouple(Coupler coupler, bool playAudio, bool dueToBrokenCouple, bool viaChainInteraction)
    {
        ushort couplerNetId = coupler.train.GetNetId();

        if (couplerNetId == 0)
        {
            LogWarning($"SendTrainUncouple failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendTrainUncouple({coupler.train.ID}, {coupler.isFrontCoupler}, {dueToBrokenCouple}, {viaChainInteraction})");

        CommitObservedTrainTopology();
    }

    public void SendHoseDisconnected(Coupler coupler, bool playAudio)
    {
        ushort couplerNetId = coupler.train.GetNetId();

        if (couplerNetId == 0)
        {
            LogWarning($"SendHoseDisconnected failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendHoseDisconnected({coupler.train.ID}, {coupler.isFrontCoupler}, {playAudio})");

        CommitObservedTrainTopology();
    }

    public void SendCockState(ushort netId, Coupler coupler, bool isOpen)
    {
        CommitObservedTrainTopology();
    }

    public void CommitObservedTrainTopology(params ushort[] controlCars)
    {
        trainTopologyCommitPending = true;
        foreach (ushort carNetId in controlCars ?? Array.Empty<ushort>())
            if (carNetId != 0)
                pendingTrainTopologyControlCars.Add(carNetId);
    }

    public void SendTrainControlAuthorityUpdate(ushort netId, uint portNetId, ControlAuthorityState state, ServerPlayer sendToPlayer = null, ServerPlayer excludePlayer = null)
    {
        var packet = new ClientboundTrainControlAuthorityUpdatePacket
        {
            NetId = netId,
            PortNetId = portNetId,
            State = state
        };

        if (sendToPlayer == null)
            if (excludePlayer == null)
                SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludeSelf: false);
            else
                SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTrainSets, excludePlayer.Peer, false);
        else
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobsCreatePacket(NetworkedStationController networkedStation, NetworkedJob[] jobs, ITransportPeer peer = null)
    {
        Log($"Sending JobsCreatePacket for stationNetId {networkedStation.NetId} with {jobs.Count()} jobs");

        var packet = ClientboundJobsCreatePacket.FromNetworkedJobs(networkedStation, jobs);

        if (peer == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForJobs, excludeSelf: true);
        else
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobsUpdatePacket(uint stationNetId, NetworkedJob[] jobs)
    {
        Multiplayer.Log($"Sending JobsUpdatePacket for stationNetId {stationNetId} with {jobs.Count()} jobs");
        SendPacketToAll(ClientboundJobsUpdatePacket.FromNetworkedJobs(stationNetId, jobs), DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForJobs, excludeSelf: true);
    }

    public void SendTaskUpdate(ushort taskNetId, TaskState newState, float taskStartTime, float taskFinishTime)
    {
        Multiplayer.Log($"Sending TaskUpdate for taskNetId {taskNetId}, newState {newState}");
        SendPacketToAll
        (
            new ClientboundTaskUpdatePacket
            {
                TaskNetId = taskNetId,
                NewState = newState,
                TaskStartTime = taskStartTime,
                TaskFinishTime = taskFinishTime
            },
            DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForJobs,
            excludeSelf: true
        );
    }

    public void SendItemsBulkUpdatePacket(List<ItemUpdateData> items, ServerPlayer player)
    {
        DebugRuntime.Publish("item", "item.bulk-send-requested", DebugRuntimeSide.Server, data: new() { ["count"] = items?.Count ?? 0, ["playerId"] = player?.PlayerId ?? 0 });
        Log($"Sending SendItemsBulkUpdatePacket with {items?.Count()} items to {player?.Username ?? "all players"}");

        var packet = new CommonItemsBulkUpdatePacket { Items = items };

        LogDebug(() =>
        {
            string debug = "Items:\r\n";

            foreach (var item in packet?.Items)
            {
                debug += "UpdateType: " + item?.UpdateType + "\r\n";
                debug += "itemNetId: " + item?.ItemNetId + "\r\n";
                debug += "PrefabName: " + item?.PrefabName + "\r\n";
                debug += "Equipped: " + item?.ItemState + "\r\n";
                debug += "Position: " + item?.ItemPosition + "\r\n";
                debug += "Rotation: " + item?.ItemRotation + "\r\n";
                debug += "ThrowDirection: " + item?.ThrowDirection + "\r\n";
                debug += "Player: " + item?.PlayerId + "\r\n";
                debug += "CarNetId: " + item?.CarNetId + "\r\n";
                debug += "AttachedFront: " + item?.AttachedFront + "\r\n";

                debug += $"States: {item?.States?.Count}\r\n";

                if (item.States != null)
                    foreach (var state in item?.States)
                        debug += "\t" + state.Key + ": " + state.Value + "\r\n";
                else
                    debug += "\r\n";
            }

            return debug;
        });

        if (player == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForItems, excludeSelf: true);
        else
        {
            TraceItemDeliveryExpected(packet, player.Peer, DeliveryMethod.ReliableOrdered);
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendItemUpdatePacket(ItemUpdateData item, ServerPlayer sendToPlayer = null, ServerPlayer excludePlayer = null)
    {
        DebugRuntime.Publish("item", "item.relay-requested", DebugRuntimeSide.Server, entityType: "Item", entityId: item?.ItemNetId.ToString(),
            data: Merge(DebugTrace.ItemSnapshotData(item), new() { ["sendToPlayer"] = sendToPlayer?.PlayerId ?? 0, ["excludePlayer"] = excludePlayer?.PlayerId ?? 0 }));
        Log($"Sending CommonItemUpdatePacket to {sendToPlayer?.Username ?? "all players"}");

        var packet = new CommonItemUpdatePacket { ItemData = item };
        if (sendToPlayer == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForItems, excludePlayer?.Peer, excludeSelf: true);
        else
        {
            TraceItemDeliveryExpected(packet, sendToPlayer.Peer, DeliveryMethod.ReliableOrdered);
            SendPacket(sendToPlayer.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendTrainsetRelocation(ClientboundTrainsetRelocationPacket packet)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
        ClientboundTrainTopologySnapshotPacket topology = trainTopologyAuthority.CommitObserved();
        SendPacketToAll(topology, DeliveryMethod.ReliableOrdered,
            PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
        lastBroadcastTrainTopologyRevision = topology.TopologyRevision;
    }

    private void OnServerboundTrainsetRelocationAckPacket(
        ServerboundTrainsetRelocationAckPacket packet, ITransportPeer peer)
    {
        peerToPlayer.TryGetValue(peer, out ServerPlayer player);
        DebugRuntime.Publish("train-relocation", "train-relocation.ack-received",
            DebugRuntimeSide.Server,
            packet.Status == 0 || packet.Status == 1 ? DebugSeverity.Info : DebugSeverity.Error,
            entityId: "train:" + packet.RootNetId, correlationId: packet.OperationId,
            data: new()
            {
                ["revision"] = packet.Revision, ["status"] = packet.Status,
                ["reasonCode"] = packet.ReasonCode ?? string.Empty,
                ["appliedHash"] = packet.AppliedHash,
                ["playerId"] = player?.PlayerId ?? 0,
                ["playerName"] = player?.Username ?? string.Empty
            });
    }

    public void SendItemSpatialLease(NetworkedItem item, ClientboundItemSpatialLeasePacket packet)
    {
        if (item == null || packet?.State == null)
            return;
        foreach (ServerPlayer player in ServerPlayers)
        {
            if (player?.Peer == null || player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;
            bool simulator = player.PlayerId == packet.State.SimulatorPlayerId;
            if (!simulator && !player.KnownItems.ContainsKey(item))
                continue;
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendItemSpatialLease(ServerPlayer player, ClientboundItemSpatialLeasePacket packet)
    {
        if (player?.Peer == null || packet?.State == null ||
            player.LoadingState < PlayerLoadingState.ReadyForItems)
            return;
        SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendItemSpatialSample(NetworkedItem item, ClientboundItemSpatialSamplePacket packet,
        byte simulatorPlayerId)
    {
        if (item == null || packet?.State == null)
            return;
        foreach (ServerPlayer player in ServerPlayers)
        {
            if (player?.Peer == null || player.PlayerId == simulatorPlayerId ||
                player.LoadingState < PlayerLoadingState.ReadyForItems || !player.KnownItems.ContainsKey(item))
                continue;
            // Ordering is per item via simulationEpoch/sampleSequence. LiteNetLib's Sequenced
            // channel is connection-wide and would let a busy item suppress another item's
            // samples, so use ordinary unreliable delivery here.
            SendPacket(player.Peer, packet, DeliveryMethod.Unreliable);
        }
    }

    public void SendItemSpatialCommit(NetworkedItem item, ClientboundItemSpatialCommitPacket packet)
    {
        if (item == null || packet?.State == null)
            return;
        foreach (ServerPlayer player in ServerPlayers)
        {
            if (player?.Peer == null || player.LoadingState < PlayerLoadingState.ReadyForItems ||
                player.PlayerId != packet.State.SimulatorPlayerId && !player.KnownItems.ContainsKey(item))
                continue;
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendLostItemsSnapshot(ServerPlayer player, uint requestId = 0)
    {
        if (player?.Peer == null) return;
        LostItemData[] items = NetworkedLostAndFoundManager.SnapshotFor(player);
        SendPacket(player.Peer, new ClientboundLostItemsSnapshotPacket
        {
            RequestId = requestId,
            Generation = NetworkedLostAndFoundManager.Generation,
            Handles = items.Select(item => item.Handle).ToArray(),
            NetIds = items.Select(item => item.NetId).ToArray(),
            Revisions = items.Select(item => item.Revision).ToArray(),
            PrefabNames = items.Select(item => item.PrefabName ?? string.Empty).ToArray(),
            DisplayNames = items.Select(item => item.DisplayName ?? string.Empty).ToArray(),
            Reasons = items.Select(item => item.Reason).ToArray(),
            LostUtcTicks = items.Select(item => item.LostUtcTicks).ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobReportArtifact(JobReportArtifactData artifact, ServerPlayer player)
    {
        if (artifact == null || player?.Peer == null)
            return;

        DebugRuntime.Publish("job", "job.report-artifact-sent", DebugRuntimeSide.Server,
            entityType: "Item", entityId: artifact.ItemNetId.ToString(), data: new Dictionary<string, object>
            {
                ["artifactToken"] = artifact.ArtifactToken,
                ["jobNetId"] = artifact.JobNetId,
                ["recipientPlayerId"] = player.PlayerId,
                ["hasDebt"] = artifact.Debt != null
            });
        SendPacket(player.Peer, new ClientboundJobReportArtifactPacket
        {
            Payload = artifact.Serialize()
        }, DeliveryMethod.ReliableOrdered);
    }

    private void TraceItemDeliveryExpected<T>(T packet, ITransportPeer peer, DeliveryMethod deliveryMethod)
    {
        if (!DebugRuntime.EnabledFor("item") || peer == null || !TryGetServerPlayer(peer, out ServerPlayer recipient)) return;
        IEnumerable<ItemUpdateData> snapshots = packet switch
        {
            CommonItemUpdatePacket single when single.ItemData != null => new[] { single.ItemData },
            CommonItemsBulkUpdatePacket bulk when bulk.Items != null => bulk.Items.Where(item => item != null),
            _ => Array.Empty<ItemUpdateData>()
        };
        foreach (ItemUpdateData snapshot in snapshots)
        {
            Dictionary<string, object> data = DebugTrace.ItemSnapshotData(snapshot);
            data["packetType"] = packet.GetType().Name;
            data["recipientPlayerId"] = recipient.PlayerId;
            data["recipientPlayerName"] = recipient.Username ?? string.Empty;
            data["recipientPeerId"] = peer.Id;
            data["delivery"] = deliveryMethod.ToString();
            data["loadingState"] = recipient.LoadingState.ToString();
            if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem networkedItem) && networkedItem != null)
            {
                bool nearby = recipient.NearbyItems.TryGetValue(networkedItem, out float nearbyTime);
                bool known = recipient.KnownItems.TryGetValue(networkedItem, out uint knownTick);
                data["nearby"] = nearby; data["nearbyAgeSeconds"] = nearby ? Time.time - nearbyTime : 0f;
                data["known"] = known; data["knownTick"] = knownTick; data["lastDirtyTick"] = networkedItem.LastDirtyTick;
                data["decision"] = !nearby ? "outside-interest-but-sent" : !known ? "create-required" : knownTick < networkedItem.LastDirtyTick ? "full-sync-required" : "relay/update";
            }
            DebugRuntime.Publish("item", "item.delivery-expected", DebugRuntimeSide.Server, entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: data);
        }
    }

    private static Dictionary<string, object> Merge(Dictionary<string, object> target, Dictionary<string, object> additions)
    {
        foreach (var pair in additions) target[pair.Key] = pair.Value;
        return target;
    }

    public void SendPitStopBulkDataPacket(ushort netId, int carCount, int carIndex, int faucetNotch, LocoResourceModuleData[] stationData, PitStopPlugData[] plugData, ServerPlayer player)
    {
        LogDebug(() => $"SendPitStopBulkDataPacket({netId}, {carCount}, {carIndex}, {faucetNotch}, {stationData.Count()}, {plugData.Count()}, {player})");

        var packet = new ClientboundPitStopBulkUpdatePacket
        {
            NetId = netId,
            CarCount = carCount,
            CarSelection = carIndex,
            FaucetNotch = faucetNotch,
            ResourceData = stationData,
            PlugData = plugData,
        };

        if (player.Peer != SelfPeer)
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopInteractionPacket(ServerPlayer player, CommonPitStopInteractionPacket packet)
    {
        LogDebug(() => $"SendPitStopInteractionPacket({player.Username}, {packet.NetId})");

        SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopPlugInteractionPacket(ServerPlayer player, CommonPitStopPlugInteractionPacket packet)
    {
        LogDebug(() => $"SendPitStopPlugInteractionPacket({packet.NetId}, {packet.InteractionType}, {packet.PlayerId}, {packet.Position}, {packet.Rotation}, {packet.TrainCarNetId}, {packet.SocketIndex}, {packet.YankForce}, {packet.YankMode})");
        SendNetSerializablePacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendCashRegisterAction(CommonCashRegisterWithModulesActionPacket packet, ServerPlayer[] players = null)
    {
        if (players == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, true);
        else
            foreach (var player in players)
                SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendGenericSwitchState(uint netId, bool isOn, ServerPlayer player = null)
    {
        var packet = new CommonGenericSwitchStatePacket
        {
            NetId = netId,
            IsOn = isOn
        };

        if (player != null)
            SendPacket(player.Peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, deliveryMethod: DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, true);
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
            NetworkLifecycle.Instance.Server.SendPacket
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

    #endregion

    #region Listeners

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

        Log($"Processing login packet for {packet.Username} ({guid}){(Multiplayer.Settings.LogIps ? $" at {request.RemoteEndPoint.Address}" : "")}");

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

        if (packet.BuildVersion != MainMenuControllerPatch.MenuProvider.BuildVersionString)
        {
            LogWarning($"Denied login to incorrect game version! Got: {packet.BuildVersion}, expected: {MainMenuControllerPatch.MenuProvider.BuildVersionString}");
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__GAME_VERSION_KEY,
                ReasonArgs = [MainMenuControllerPatch.MenuProvider.BuildVersionString, packet.BuildVersion.ToString()]
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        // The same player reconnecting (e.g. after a crash) before their old connection has timed
        // out. Retire the stale session now: left alone it holds their slot, and when it finally
        // times out its cleanup unbinds item ownership by identity - including from the new session.
        ServerPlayer stale = ServerPlayers.FirstOrDefault(player =>
            guid != Guid.Empty && player.Guid == guid && player.Peer != SelfPeer);
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
            ClientboundLoginResponsePacket denyPacket = new()
            {
                ReasonKey = Locale.DISCONN_REASON__FULL_SERVER_KEY
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        var validation = ModCompatibilityManager.Instance.ValidateClientMods(packet.Mods);
        if (!validation.IsValid)
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
            return;
        }

        // Unpause physics
        if (AppUtil.Instance.IsTimePaused)
            AppUtil.Instance.RequestSystemOnValueChanged(0.0f);

        ITransportPeer peer = request.Accept();

        ServerPlayer serverPlayer = new
        (
            peer,
            overrideUsername,
            packet.Username,
            guid,
            packet.CharacterId,
            packet.IsVR
        );

        serverPlayers.Add(serverPlayer.PlayerId, serverPlayer);
        peerToPlayer.Add(peer, serverPlayer);
        AuthoritativeItemRegistry.RebindPersistentOwner(serverPlayer);
        NetworkedLostAndFoundManager.OnOwnerConnected(serverPlayer);
        WorldItemPersistenceManager.OnOwnerConnected(serverPlayer);

        ClientboundLoginResponsePacket acceptPacket = new()
        {
            Accepted = true,
            PlayerId = serverPlayer.PlayerId,
            OverrideUsername = serverPlayer.OriginalUsername == serverPlayer.Username ? string.Empty : overrideUsername,
            ProtocolFingerprint = ProtocolManifestProvider.Current.Fingerprint,
        };

        SendPacket(peer, acceptPacket, DeliveryMethod.ReliableUnordered);
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
                HazmatSync.Server_SendAll(peer);
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
        {
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

            SendPacketToAll(clientboundPlayerJoinedPacket, DeliveryMethod.ReliableOrdered, PlayerLoadingState.Complete, peer);

            // Announce player joined
            ChatManager.ServerMessage(player.Username + " joined the game", null, player);

            // Send existing players
            foreach (ServerPlayer otherPlayer in ServerPlayers)
            {
                if (player.PlayerId == otherPlayer.PlayerId)
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
    }

    private void OnServerboundPlayerPositionPacket(ServerboundPlayerPositionPacket packet, ITransportPeer peer)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Server, "Player", peer?.Id.ToString());
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            DebugTrace.Validation("player", "Player", peer?.Id.ToString(), false, "unknown-player", DebugRuntimeSide.Server);
            LogWarning($"Received Player Position from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

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
        }, DeliveryMethod.Sequenced, PlayerLoadingState.Complete, peer);
    }

    private void OnServerboundPlayerPreferenceUpdatePacket(ServerboundPlayerPreferenceUpdatePacket packet, ITransportPeer peer)
    {
        Dictionary<PlayerPreference, string> preferences = [];

        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"Received Player Preferences Update from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

        // Store the characterId for other players connecting to the server
        if (packet.GetPreferencesDictionary().TryGetValue(PlayerPreference.CharacterModel, out string characterId))
        {
            player.CharacterId = characterId;
            preferences.Add(PlayerPreference.CharacterModel, characterId);
        }

        if (preferences.Count > 0)
            SendPlayerPreferencesUpdate(player, preferences);
    }

    private void OnServerboundTimeAdvancePacket(ServerboundTimeAdvancePacket packet, ITransportPeer peer)
    {
        if (packet.IsSleep)
        {
            OnSleepRequest(packet, peer);
            return;
        }

        if (!fastTravelAdvancesTime)
        {
            TryGetServerPlayer(peer, out ServerPlayer player);
            LogWarning($"Player {player?.Username} sent a TimeAdvance request, but FastTravelAdvancesTime is disabled");
            return;
        }

        SendPacketToAll
        (
            new ClientboundTimeAdvancePacket
            {
                amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds
            },
            DeliveryMethod.ReliableUnordered,
            PlayerLoadingState.ReadyForWorldState,
            peer
        );
    }

    // One sleep cooldown for everyone, kept by the host. Each game used to check only its own last
    // wake-up (and a client's started empty on every join), so players could sleep one after another
    // and skip as much time as they liked, making everyone's jobs late.
    private void OnSleepRequest(ServerboundTimeAdvancePacket packet, ITransportPeer peer)
    {
        TryGetServerPlayer(peer, out ServerPlayer player);
        BedSleepingController sleep = BedSleepingController.Instance;
        if (sleep == null || packet.amountOfTimeToSkipInSeconds <= 0 || packet.amountOfTimeToSkipInSeconds > 24 * 3600)
            return;

        // The host's own sleep has already happened here (vanilla checked the cooldown and set the
        // wake time): pass the time skip on and share the wake time.
        if (player != null && NetworkLifecycle.Instance.IsHost(player))
        {
            SendPacketToAll(new ClientboundTimeAdvancePacket { amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds },
                DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForWorldState, peer);
            SendSleepState();
            return;
        }

        DateTime now = sleep.GetCurrentIngameTime();
        bool disabled = Globals.G.GameParams.SleepCooldownInHours < 0;
        double sinceWake = (now - sleep.lastWakeTime).TotalSeconds;
        double cooldown = sleep.GetMinimumTimeSinceLastWakeSeconds();
        if (disabled || sinceWake < cooldown)
        {
            LogWarning($"{player?.Username} tried to sleep, but {(disabled ? "sleeping is disabled" : $"the shared cooldown has {cooldown - sinceWake:N0}s left")}");
            // Their game skipped the time locally; the weather sync puts their clock back.
            SendSleepState(peer);
            SendWeatherState(peer);
            return;
        }

        SendPacketToAll(new ClientboundTimeAdvancePacket { amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds },
            DeliveryMethod.ReliableUnordered, PlayerLoadingState.ReadyForWorldState, peer);
        sleep.lastWakeTime = now.AddSeconds(packet.amountOfTimeToSkipInSeconds);
        sleep.lastSleepDurationSeconds = (int)packet.amountOfTimeToSkipInSeconds;
        Log($"{player?.Username} slept {packet.amountOfTimeToSkipInSeconds / 3600f:N1}h; the next sleep for anyone is after {sleep.lastWakeTime.AddSeconds(sleep.GetMinimumTimeSinceLastWakeSeconds())}");
        SendSleepState();
    }

    public void SendHazmatTiles(byte[] data, ITransportPeer peer)
    {
        ClientboundHazmatTilesPacket packet = new() { Data = data };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForTiles, excludeSelf: true);
    }

    private void OnServerboundHazmatIgnitePacket(ServerboundHazmatIgnitePacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HazmatSync.Server_Ignite(new HazmatSync.ServerPlayerIgnite(packet.GridCoords, packet.Strength, player.AbsoluteWorldPosition));
    }

    public void SendSleepState(ITransportPeer peer = null)
    {
        BedSleepingController sleep = BedSleepingController.Instance;
        if (sleep == null)
            return;
        ClientboundSleepStatePacket packet = new()
        {
            LastWakeTimeOADate = sleep.lastWakeTime.ToOADate(),
            LastSleepDurationSeconds = sleep.lastSleepDurationSeconds
        };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    private void OnCommonChangeJunctionPacket(CommonChangeJunctionPacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, peer);
    }

    private void OnCommonRotateTurntablePacket(CommonRotateTurntablePacket packet, ITransportPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, peer);
    }

    private void OnCommonCouplerInteractionPacket(CommonCouplerInteractionPacket packet, ITransportPeer peer)
    {
        if (!peerToPlayer.TryGetValue(peer, out var player))
        {
            LogWarning($"Received Coupler Interaction from {peer.GetType()}, peerId: {peer.Id}, but could not find matching player.");
            return;
        }

        //todo: add validation that to ensure the client is near the coupler - this packet may also be used for remote operations and may need to factor that in in the future
        if (NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            if (netTrainCar.Server_ValidateCouplerInteraction(packet, player))
            {
                // The listen host owns the canonical train topology. Relaying a client-local
                // coupling without applying it here lets the requesting client join two cars
                // while the host simulation continues to move them independently.
                netTrainCar.SuppressAuthoritativeTopologyObservations();
                if (packet.OtherNetId != 0 &&
                    NetworkedTrainCar.TryGet(packet.OtherNetId, out NetworkedTrainCar otherNetTrainCar))
                    otherNetTrainCar.SuppressAuthoritativeTopologyObservations();
                using (NetworkedTrainCar.BeginAuthoritativeTopologyApplication())
                    netTrainCar.Common_ReceiveCouplerInteraction(packet);

                CouplerInteractionType flags = (CouplerInteractionType)packet.Flags;
                CommitObservedTrainTopology();
                // Chain dragging/screw interaction remains transient presentation. Canonical
                // endpoint state above is the recovery/acknowledgement path.
                if (!flags.HasFlag(CouplerInteractionType.CoupleViaUI) &&
                    !flags.HasFlag(CouplerInteractionType.UncoupleViaUI))
                    SendPacketToAll(packet, DeliveryMethod.ReliableOrdered,
                        PlayerLoadingState.ReadyForTrainSets, peer, excludeSelf: true);
            }
            else
            {
                LogDebug(() => $"OnCommonCouplerInteractionPacket([{packet.Flags}, {netTrainCar.CurrentID}, {packet.NetId}], {player.PlayerId}) Sending validation failure");
                //failed validation notify client
                SendPacket
                (
                    peer,
                    new CommonCouplerInteractionPacket
                    {
                        NetId = packet.NetId,
                        Flags = (ushort)CouplerInteractionType.NoAction,
                        IsFrontCoupler = packet.IsFrontCoupler,
                    },
                    DeliveryMethod.ReliableOrdered
                );
                SendPacket(peer, trainTopologyAuthority.Snapshot(), DeliveryMethod.ReliableOrdered);
            }
        }
        else
        {
            LogWarning($"OnCommonCouplerInteractionPacket([{packet.Flags}, {packet.NetId}], " +
                $"{player.PlayerId}) rejected unknown train car");
        }
    }

    private static bool TryResolveCoupler(ushort netId, bool front, out Coupler coupler)
    {
        coupler = null;
        if (!NetworkedTrainCar.TryGet(netId, out TrainCar car) || car == null)
            return false;
        coupler = front ? car.frontCoupler : car.rearCoupler;
        return coupler != null;
    }

    private void OnCommonBrakeCylinderReleasePacket(CommonBrakeCylinderReleasePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            return;
        TrainControlCommitResult result = ValidateControlEnvelope(player, car,
            packet.ExpectedRevision, packet.OperationId, requireLease: false, null);
        if (result == TrainControlCommitResult.Accepted)
        {
            try
            {
                if (!NetworkLifecycle.Instance.IsHost(player))
                    car.TrainCar.brakeSystem.ReleaseBrakeCylinderPressure();
                if (!car.TrainCar.brakeSystem.controlReservoirPressureReleased)
                    result = TrainControlCommitResult.InvalidValue;
            }
            catch
            {
                result = TrainControlCommitResult.InvalidValue;
            }
        }
        ClientboundTrainControlCommitPacket commit = ControlCommit(car, packet.OperationId,
            result, brakeCylinderReleased: result == TrainControlCommitResult.Accepted);
        SendControlCommit(commit, peer);
    }

    private void OnCommonHandbrakePositionPacket(CommonHandbrakePositionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            return;
        TrainControlCommitResult result = float.IsNaN(packet.Position) || float.IsInfinity(packet.Position)
            ? TrainControlCommitResult.InvalidValue
            : ValidateControlEnvelope(player, car, packet.ExpectedRevision,
                packet.OperationId, requireLease: false, null, skipRangeCheck: packet.ViaRemote);
        if (result == TrainControlCommitResult.Accepted &&
            !NetworkLifecycle.Instance.IsHost(player))
            car.TrainCar.brakeSystem.SetHandbrakePosition(Mathf.Clamp01(packet.Position));
        ClientboundTrainControlCommitPacket commit = ControlCommit(car, packet.OperationId, result,
            includesHandbrake: true);
        SendControlCommit(commit, peer);
    }

    private void OnCommonPaintThemePacket(CommonPaintThemePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
            return;

        if (!PaintThemeLookup.Instance.TryGet(packet.PaintThemeId, out PaintTheme paint) || paint == null)
        {
            LogWarning($"Received paint theme change for {netTrainCar?.CurrentID}, but paint theme id '{packet.PaintThemeId}' does not exist.");
            return;
        }

        Log($"Received paint theme change for {netTrainCar?.CurrentID}, theme '{paint.AssetName}'");

        LogDebug(() => $"OnCommonPaintThemePacket() [{netTrainCar?.CurrentID}, {packet.NetId}], area: {packet.TargetArea}, paint: [{paint?.AssetName}, {packet.PaintThemeId}]");

        netTrainCar?.Server_ValidatePaintThemeChange(packet.TargetArea, paint, player);

        //SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnServerboundAddCoalPacket(ServerboundAddCoalPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        //is value valid?
        if (float.IsNaN(packet.CoalMassDelta))
            return;

        if (!NetworkLifecycle.Instance.IsHost(player))
        {
            //is player close enough to add coal?
            if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
                networkedTrainCar.firebox?.fireboxCoalControlPort.ExternalValueUpdate(packet.CoalMassDelta);
        }
    }

    private void OnServerboundTenderCoalPacket(ServerboundTenderCoalPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        // is value valid?
        if (float.IsNaN(packet.CoalMassDelta))
            return;

        if (!NetworkLifecycle.Instance.IsHost(player))
        {
            //is player close enough to add/remove coal?
            if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
                networkedTrainCar.coalPile?.coalConsumePort.ExternalValueUpdate(packet.CoalMassDelta);
        }
    }

    private void OnServerboundFireboxIgnitePacket(ServerboundFireboxIgnitePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        if (!NetworkLifecycle.Instance.IsHost(player))
        {
            //is player close enough to ignite firebox?
            if ((player.WorldPosition - networkedTrainCar.transform.position).sqrMagnitude <= networkedTrainCar.CarLengthSq)
                networkedTrainCar.firebox?.Ignite();
        }
    }

    private void OnCommonTrainPortsPacket(CommonTrainPortsPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        bool shapeValid = packet.PortIds != null && packet.PortValues != null &&
                          packet.PortIds.Length > 0 &&
                          packet.PortIds.Length == packet.PortValues.Length &&
                          packet.PortValues.All(value => !float.IsNaN(value)); // infinity is legitimate (e.g. DE traction motor resistance at rest)
        TrainControlCommitResult result = shapeValid
            ? ValidateControlEnvelope(player, networkedTrainCar, packet.ExpectedRevision,
                packet.OperationId, requireLease: true, packet.PortIds)
            : TrainControlCommitResult.InvalidValue;
        if (result == TrainControlCommitResult.Accepted &&
            !NetworkLifecycle.Instance.IsHost(player))
        {
            if (!networkedTrainCar.Server_ValidateClientSimFlowPacket(player, packet))
                result = TrainControlCommitResult.AuthorityDenied;
            else
                networkedTrainCar.Common_UpdatePorts(packet);
        }
        if (!networkedTrainCar.TryCapturePortValues(packet.PortIds ?? Array.Empty<uint>(),
                out float[] acceptedValues))
            result = TrainControlCommitResult.UnknownControl;
        ClientboundTrainControlCommitPacket commit = ControlCommit(networkedTrainCar,
            packet.OperationId, result, packet.PortIds, acceptedValues);
        SendControlCommit(commit, peer);
    }

    private void OnServerboundTrainControlAuthorityPacket(ServerboundTrainControlAuthorityPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Server_ReceiveAuthorityRequest(packet.PortNetId, player, packet.RequestAuthority);
    }

    private void OnCommonTrainFusesPacket(CommonTrainFusesPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) ||
            !NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar car))
            return;
        bool shapeValid = packet.FuseIds != null && packet.FuseValues != null &&
                          packet.FuseIds.Length > 0 &&
                          packet.FuseIds.Length == packet.FuseValues.Length;
        TrainControlCommitResult result = shapeValid
            ? ValidateControlEnvelope(player, car, packet.ExpectedRevision,
                packet.OperationId, requireLease: false, null)
            : TrainControlCommitResult.InvalidValue;
        if (result == TrainControlCommitResult.Accepted && !NetworkLifecycle.Instance.IsHost(player))
            car.Common_UpdateFuses(packet);
        if (!car.TryCaptureFuseValues(packet.FuseIds ?? Array.Empty<uint>(), out bool[] acceptedValues))
            result = TrainControlCommitResult.UnknownControl;
        ClientboundTrainControlCommitPacket commit = ControlCommit(car, packet.OperationId,
            result, fuseIds: packet.FuseIds, fuseValues: acceptedValues);
        SendControlCommit(commit, peer);
    }

    private TrainControlCommitResult ValidateControlEnvelope(ServerPlayer player,
        NetworkedTrainCar car, uint expectedRevision, ulong operationId, bool requireLease,
        uint[] portIds, bool skipRangeCheck = false)
    {
        if (player == null || car?.TrainCar == null)
            return TrainControlCommitResult.UnknownControl;
        if (!skipRangeCheck && !NetworkLifecycle.Instance.IsHost(player) &&
            (player.WorldPosition - car.transform.position).sqrMagnitude > car.CarLengthSq)
            return TrainControlCommitResult.OutOfRange;
        if (requireLease && !car.Server_HasPortAuthority(player, portIds))
            return TrainControlCommitResult.AuthorityDenied;
        var key = (player.PlayerId, car.NetId);
        bool orderedOperationStream = operationId != 0 &&
            (!lastTrainControlOperation.TryGetValue(key, out ulong previous) || operationId > previous);
        if (expectedRevision != car.ControlRevision && !orderedOperationStream)
            return TrainControlCommitResult.StaleRevision;
        lastTrainControlOperation[key] = operationId;
        return TrainControlCommitResult.Accepted;
    }

    private static ClientboundTrainControlCommitPacket ControlCommit(NetworkedTrainCar car,
        ulong operationId, TrainControlCommitResult result, uint[] portIds = null,
        float[] portValues = null, uint[] fuseIds = null, bool[] fuseValues = null,
        bool includesHandbrake = false, bool brakeCylinderReleased = false)
    {
        uint revision = result == TrainControlCommitResult.Accepted
            ? car.Server_CommitControlRevision()
            : car.ControlRevision;
        return new ClientboundTrainControlCommitPacket
        {
            NetId = car.NetId,
            Revision = revision,
            OperationId = operationId,
            Result = result,
            PortIds = portIds ?? Array.Empty<uint>(),
            PortValues = portValues ?? Array.Empty<float>(),
            FuseIds = fuseIds ?? Array.Empty<uint>(),
            FuseValues = fuseValues ?? Array.Empty<bool>(),
            IncludesHandbrake = includesHandbrake,
            HandbrakePosition = car.TrainCar.brakeSystem.handbrakePosition,
            BrakeCylinderReleased = brakeCylinderReleased
        };
    }

    private void SendControlCommit(ClientboundTrainControlCommitPacket commit, ITransportPeer requester)
    {
        if (commit.Result == TrainControlCommitResult.Accepted)
            SendPacketToAll(commit, DeliveryMethod.ReliableOrdered,
                PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
        else
            SendPacket(requester, commit, DeliveryMethod.ReliableOrdered);
    }

    // The comms radio reaches about 100 m; allow for the car moving while the request travels.
    private const float MaxSandboxCargoDistance = 200f;

    private void OnServerboundSandboxCargoPacket(ServerboundSandboxCargoPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        // The loader is a sandbox tool: only when the host's settings make it available.
        GameParams gameParams = Globals.G.GameParams;
        bool freeRoam = DV.UserManagement.UserManager.Instance.CurrentUser.CurrentSession.GameMode.Equals("FreeRoam");
        if (!(gameParams.CommsRadioSandboxCheatMode && freeRoam || gameParams.CommsRadioCheatMode))
        {
            LogWarning($"{player.Username} used the sandbox cargo loader, but it isn't enabled");
            return;
        }
        if (!NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar car) || car == null || car.logicCar == null)
            return;
        if ((player.WorldPosition - car.transform.position).sqrMagnitude > MaxSandboxCargoDistance * MaxSandboxCargoDistance)
        {
            LogWarning($"{player.Username} used the sandbox cargo loader on {car.ID} from too far away");
            return;
        }

        if (car.LoadedCargo != CargoType.None)
            car.logicCar.UnloadCargo(car.LoadedCargoAmount, car.LoadedCargo);
        if (packet.CargoTypeNetId == 0)
            return;
        if (!CargoTypeLookup.Instance.TryGet(packet.CargoTypeNetId, out CargoType_v2 cargo) || cargo == null ||
            !cargo.IsLoadableOnCarType(car.carLivery.parentType))
        {
            LogWarning($"{player.Username} tried to load cargo {packet.CargoTypeNetId} that {car.ID} can't carry");
            return;
        }
        car.logicCar.LoadCargo(car.cargoCapacity, cargo.v1);
    }

    private void OnServerboundTrainSyncRequestPacket(ServerboundTrainSyncRequestPacket packet,
        ITransportPeer peer)
    {
        if (NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
        {
            networkedTrainCar.Server_DirtyAllState();
            SendPacket(peer, trainTopologyAuthority.Snapshot(), DeliveryMethod.ReliableOrdered);
        }
    }

    // Couplers this far apart can't be joined (vanilla couples within 1.5 m); slack for latency.
    private const float MaxTopologyCouplerGap = 4f;

    private void OnServerboundTrainTopologyCommandPacket(
        ServerboundTrainTopologyCommandPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player) || packet == null)
            return;

        bool IsInRange()
        {
            if (NetworkLifecycle.Instance.IsHost(player))
                return true;
            if (!NetworkedTrainCar.TryGet(packet.PrimaryCarNetId,
                    out NetworkedTrainCar primary) || primary?.TrainCar == null)
                return false;
            // A remote controller is used from a distance; only the coupler check below applies.
            if (!packet.ViaRemote &&
                (player.WorldPosition - primary.transform.position).sqrMagnitude > primary.CarLengthSq)
                return false;
            if (!packet.HasSecondary)
                return true;
            // The player acts from the primary car (often its cab, so the other car's centre can be
            // well over a car length away); what matters for the second car is that its coupler is
            // actually next to the primary's, which also stops a lagging view coupling across a gap.
            if (!NetworkedTrainCar.TryGet(packet.SecondaryCarNetId, out NetworkedTrainCar secondary) ||
                secondary?.TrainCar == null)
                return false;
            Coupler primaryCoupler = packet.PrimaryEnd == TrainEnd.Front
                ? primary.TrainCar.frontCoupler : primary.TrainCar.rearCoupler;
            Coupler secondaryCoupler = packet.SecondaryEnd == TrainEnd.Front
                ? secondary.TrainCar.frontCoupler : secondary.TrainCar.rearCoupler;
            return primaryCoupler != null && secondaryCoupler != null &&
                   (primaryCoupler.transform.position - secondaryCoupler.transform.position).sqrMagnitude <=
                   MaxTopologyCouplerGap * MaxTopologyCouplerGap;
        }

        ClientboundTrainTopologySnapshotPacket snapshot = trainTopologyAuthority.Execute(
            player.PlayerId, packet, runtimeAuthorized: () => true, runtimeInRange: IsInRange);
        if (snapshot.Result == TrainTopologyResultCode.Accepted)
        {
            SendPacketToAll(snapshot, DeliveryMethod.ReliableOrdered,
                PlayerLoadingState.ReadyForTrainSets, excludeSelf: true);
            lastBroadcastTrainTopologyRevision = snapshot.TopologyRevision;
        }
        else
            SendPacket(peer, snapshot, DeliveryMethod.ReliableOrdered);

        DebugRuntime.Publish("train", "train.topology-command-result", DebugRuntimeSide.Server,
            entityType: "Topology", entityId: packet.OperationId.ToString(),
            data: DebugValueSnapshotter.SnapshotObject(new
            {
                player.PlayerId,
                player.Username,
                packet.OperationId,
                packet.ExpectedRevision,
                committedRevision = snapshot.TopologyRevision,
                command = packet.Kind.ToString(),
                result = snapshot.Result.ToString(),
                packet.PrimaryCarNetId,
                primaryEnd = packet.PrimaryEnd.ToString(),
                packet.SecondaryCarNetId,
                secondaryEnd = packet.SecondaryEnd.ToString()
            }));
    }

    private void OnServerboundTrainTopologySyncRequestPacket(
        ServerboundTrainTopologySyncRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out _))
            return;
        SendPacket(peer, trainTopologyAuthority.Snapshot(), DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundTrainDeleteRequestPacket(ServerboundTrainDeleteRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        if (networkedTrainCar.HasPlayers)
        {
            LogWarning($"{player.Username} tried to delete a train with players in it!");
            return;
        }

        TrainCar trainCar = networkedTrainCar.TrainCar;
        float cost = trainCar.playerSpawnedCar ? 0.0f : Mathf.RoundToInt(Globals.G.GameParams.DeleteCarMaxPrice);
        if (!Inventory.Instance.RemoveMoney(cost))
        {
            LogWarning($"{player.Username} tried to delete a train without enough money to do so!");
            return;
        }

        Job job = JobsManager.Instance.GetJobOfCar(trainCar.logicCar);
        switch (job?.State)
        {
            case JobState.Available:
                job.ExpireJob();
                break;
            case JobState.InProgress:
                JobsManager.Instance.AbandonJob(job);
                break;
        }

        var garageRef = trainCar.GetComponent<HomeGarageReference>();
        if (garageRef != null && garageRef.garageCarSpawner != null)
        {
            garageRef.garageCarSpawner.ReturnCarHome(trainCar);
        }
        else
        {
            CarSpawner.Instance.DeleteCar(trainCar);
            UnusedTrainCarDeleter.Instance.ClearInvalidCarReferencesAfterManualDelete();
        }
    }

    private void OnServerboundTrainRerailRequestPacket(ServerboundTrainRerailRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;

        TrainCar trainCar = networkedTrainCar.TrainCar;
        Vector3 position = packet.Position + WorldMover.currentMove;

        //Check if player is a Newbie (currently shared with all players)
        float cost = TutorialHelper.InRestrictedMode || rerailController != null && rerailController.isPlayerNewbie ? 0f :
            RerailController.CalculatePrice((networkedTrainCar.transform.position - position).magnitude, trainCar.carType, Globals.G.GameParams.RerailMaxPrice);

        if (!Inventory.Instance.RemoveMoney(cost))
        {
            LogWarning($"{player.Username} tried to rerail a train without enough money to do so!");
            return;
        }

        trainCar.Rerail(networkedRailTrack.RailTrack, position, packet.Forward);
    }

    private void OnServerboundTrainSpawnRequestPacket(ServerboundTrainSpawnRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!NetworkedRailTrack.TryGet(packet.TrackNetId, out NetworkedRailTrack networkedRailTrack) || networkedRailTrack == null)
        {
            LogWarning($"{player.Username} tried to spawn a car on invalid track netId: {packet.TrackNetId}");
            return;
        }

        if (!Components.TrainComponentLookup.Instance.LiveryFromId(packet.LiveryId, out TrainCarLivery livery) || livery == null || livery.prefab == null)
        {
            LogWarning($"{player.Username} tried to spawn a car with invalid livery Id: {packet.LiveryId}");
            return;
        }

        // Check spawn location on track is valid
        var kinked = networkedRailTrack.RailTrack.GetKinkedPointSet().points;
        if (packet.Index < 0 || packet.Index >= kinked.Length)
        {
            LogWarning($"{player.Username} tried to spawn a car at an invalid index: {packet.Index}");
            return;
        }

        var spawnPoint = kinked[packet.Index];
        var startPoint = (Vector3)kinked.First().position;// + WorldMover.currentMove;
        var endpoint = (Vector3)kinked.Last().position;// + WorldMover.currentMove;

        // Check there's enough space for the car
        var carBounds = CarSpawner.GetBoundsOfCar(livery.prefab);
        if (!CarSpawner.IsThereSpaceForCarOnPoint(spawnPoint, startPoint, endpoint, carBounds.extents))
        {
            LogWarning($"{player.Username} tried to spawn a car, but there's no room on the track");
            return;
        }

        // Check player is within range of the spawn point
        float playerDistanceToSpawn = (player.AbsoluteWorldPosition - (Vector3)spawnPoint.position).magnitude;
        if (playerDistanceToSpawn > CommsRadioCarSpawner.SIGNAL_RANGE && !Mathf.Approximately(playerDistanceToSpawn, CommsRadioCarSpawner.SIGNAL_RANGE))
        {
            LogWarning($"{player.Username} tried to spawn a train {playerDistanceToSpawn:F2}m away (max: {CommsRadioCarSpawner.SIGNAL_RANGE}m)");
            return;
        }

        Vector3 forward = packet.WithTrackDirection ? spawnPoint.forward : -spawnPoint.forward;

        TrainCar spawnedCar = CarSpawner.Instance.SpawnCarFromRemote(livery.prefab, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward);
    }

    private void OnServerboundWorkTrainRequestPacket(ServerboundWorkTrainRequestPacket packet, ITransportPeer peer)
    {
        TrainCar trainCar;

        var rpcResponse = new SpawnResponse() { Response = SpawnResponse.ResponseType.InUse };

        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        LogDebug(() => $"OnServerboundWorkTrainRequestPacket() from : {player.Username}, trackNetId: {packet.TrackNetId}, liveryId: {packet.LiveryId}, index: {packet.Index}, withTrackDirection: {packet.WithTrackDirection}");

        if (!NetworkedRailTrack.TryGet(packet.TrackNetId, out NetworkedRailTrack networkedRailTrack) || networkedRailTrack == null)
        {
            LogWarning($"{player.Username} tried to request a work train on invalid track netId: {packet.TrackNetId}");
            return;
        }

        if (!Components.TrainComponentLookup.Instance.LiveryFromId(packet.LiveryId, out TrainCarLivery livery) || livery == null || livery.prefab == null)
        {
            LogWarning($"{player.Username} tried to request a work train with invalid livery Id: {packet.LiveryId}");
            return;
        }

        // Check if this is a garage loco
        bool isGarageCar = GarageCarSpawner.Spawners.TryGetValue(livery, out var selectedGarageSpawner);

        // Check funds now, but only charge once every other check has passed: failures used to
        // return after the money was taken (and some never answered the client's RPC).
        float price = isGarageCar
            ? Mathf.Min(selectedGarageSpawner.garageType.summonPrice, Globals.G.GameParams.WorkTrainSummonMaxPrice)
            : 0f;
        bool Charge()
        {
            if (price <= 0f || Inventory.Instance.RemoveMoney(price))
                return true;
            LogWarning($"{player.Username} tried to request a work train without enough money to do so!");
            rpcResponse.Response = SpawnResponse.ResponseType.InsufficientFunds;
            SendRpcResponse(packet.TicketId, rpcResponse, peer);
            return false;
        }
        if (price > 0f && Inventory.Instance.PlayerMoney < price)
        {
            Charge();
            return;
        }

        trainCar = isGarageCar ? selectedGarageSpawner.GetCar(livery) : CarSpawner.Instance.AllCars.FirstOrDefault(tc => tc.carLivery == livery);

        // Check spawn location on track is valid
        var kinked = networkedRailTrack.RailTrack.GetKinkedPointSet().points;
        if (packet.Index < 0 || packet.Index >= kinked.Length)
        {
            LogWarning($"{player.Username} tried to spawn a car at an invalid index: {packet.Index}");
            SendRpcResponse(packet.TicketId, rpcResponse, peer);
            return;
        }

        var spawnPoint = kinked[packet.Index];
        var startPoint = (Vector3)kinked.First().position;
        var endpoint = (Vector3)kinked.Last().position;

        // Check there's enough space for the car
        var carBounds = CarSpawner.GetBoundsOfCar(livery.prefab);
        if (!CarSpawner.IsThereSpaceForCarOnPoint(spawnPoint, startPoint, endpoint, carBounds.extents))
        {
            LogWarning($"{player.Username} tried to spawn a car, but there's no room on the track");
            SendRpcResponse(packet.TicketId, rpcResponse, peer);
            return;
        }

        // Check player is within range of the spawn point
        float playerDistanceToSpawn = (player.AbsoluteWorldPosition - (Vector3)spawnPoint.position).magnitude;
        if (playerDistanceToSpawn > CommsRadioCarSpawner.SIGNAL_RANGE && !Mathf.Approximately(playerDistanceToSpawn, CommsRadioCarSpawner.SIGNAL_RANGE))
        {
            LogWarning($"{player.Username} tried to spawn a train {playerDistanceToSpawn:F2}m away (max: {CommsRadioCarSpawner.SIGNAL_RANGE}m)");
            SendRpcResponse(packet.TicketId, rpcResponse, peer);
            return;
        }

        Vector3 forward = packet.WithTrackDirection ? spawnPoint.forward : -spawnPoint.forward;

        if (trainCar == null)
        {
            LogDebug(() => $"OnServerboundWorkTrainRequestPacket() {player.Username} tried to request a work train of {livery.id} but no existing car found, spawning new car");
            if (!Charge())
                return;
            trainCar = CarSpawner.Instance.SpawnCrewVehicle(livery, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward, selectedGarageSpawner);

            SendSpawnTrainset([trainCar], true, true);
        }
        else
        {
            // Check if is currently in use/recently used
            if (trainCar.IsTeleporting || !trainCar.isStationary)
            {
                LogDebug(() => $"OnServerboundWorkTrainRequestPacket() {player.Username} tried to request a work train of {livery.id} but the car is in use teleporting: {trainCar.IsTeleporting}, stationary: {trainCar.isStationary}");

                SendRpcResponse(packet.TicketId, rpcResponse, peer);
                return;
            }

            bool recentlyVisited = false;
            bool attachedToJob = false;
            int ctr = 0;
            var cars = trainCar.trainset.cars;
            while (!recentlyVisited && ctr < cars.Count)
            {
                var car = cars[ctr];

                if (car.IsLoco)
                {
                    var visitChecker = car.visitChecker;
                    recentlyVisited = (visitChecker != null && visitChecker.IsRecentlyVisited);
                }
                else
                {
                    var job = JobsManager.Instance.GetJobOfCar(car.logicCar);
                    attachedToJob = (job != null && job.State == JobState.InProgress);
                }
                ctr++;
            }

            if (recentlyVisited || attachedToJob)
            {
                LogDebug(() => $"OnServerboundWorkTrainRequestPacket() {player.Username} tried to request a work train of {livery.id} but the car is in use ({(recentlyVisited ? "recently visited" : "")}{(recentlyVisited && attachedToJob ? ", " : "")}{(attachedToJob ? "attached to an active job" : "")})");
                SendRpcResponse(packet.TicketId, rpcResponse, peer);
                return;
            }

            // Confirm no players are currently in the car
            if (!NetworkedTrainCar.TryGetFromTrainCar(trainCar, out var networkedTrainCar))
            {
                LogError($"OnServerboundWorkTrainRequestPacket() {player.Username} tried to request a work train of {livery.id} but NetworkedTrainCar was not found for {trainCar?.ID}");
                SendRpcResponse(packet.TicketId, rpcResponse, peer);
                return;
            }

            if (networkedTrainCar.HasPlayers)
            {
                LogDebug(() => $"OnServerboundWorkTrainRequestPacket() {player.Username} tried to request a work train of {livery.id} but the car is occupied");
                SendRpcResponse(packet.TicketId, rpcResponse, peer);
                return;
            }

            // Checks passed, call the work train
            if (!Charge())
                return;
            trainCar = CarSpawner.Instance.SpawnCrewVehicle(livery, networkedRailTrack.RailTrack, (Vector3)spawnPoint.position, forward, selectedGarageSpawner);
        }

        rpcResponse.Response = SpawnResponse.ResponseType.Success;
        SendRpcResponse(packet.TicketId, rpcResponse, peer);
    }

    private void OnServerboundLicensePurchaseRequestPacket(ServerboundLicensePurchaseRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        JobLicenseType_v2 jobLicense = null;
        GeneralLicenseType_v2 generalLicense = null;
        float? price = packet.IsJobLicense
            ? (jobLicense = Globals.G.Types.jobLicenses.Find(l => l.id == packet.Id))?.price
            : (generalLicense = Globals.G.Types.generalLicenses.Find(l => l.id == packet.Id))?.price;

        if (!price.HasValue)
        {
            LogWarning($"{player.Username} tried to purchase an invalid {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id}!");
            return;
        }

        CareerManagerDebtController.Instance.RefreshExistingDebtsState();
        if (CareerManagerDebtController.Instance.NumberOfNonZeroPricedDebts > 0)
        {
            LogWarning($"{player.Username} tried to purchase a {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id} while having existing debts!");
            return;
        }

        if (!Inventory.Instance.RemoveMoney(price.Value))
        {
            LogWarning($"{player.Username} tried to purchase a {(packet.IsJobLicense ? "job" : "general")} license with id {packet.Id} without enough money to do so!");
            return;
        }

        if (packet.IsJobLicense)
            LicenseManager.Instance.AcquireJobLicense(jobLicense);
        else
            LicenseManager.Instance.AcquireGeneralLicense(generalLicense);
    }
    private void OnServerboundJobValidateRequestPacket(ServerboundJobValidateRequestPacket packet, ITransportPeer peer)
    {
        Log($"OnServerboundJobValidateRequestPacket(): {packet.JobNetId}");

        if (!NetworkedJob.Get(packet.JobNetId, out NetworkedJob networkedJob))
        {
            LogWarning($"OnServerboundJobValidateRequestPacket() NetworkedJob not found: {packet.JobNetId}");

            SendPacket(peer, new ClientboundJobValidateResponsePacket { JobNetId = packet.JobNetId, Invalid = true, JobMissing = true }, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"OnServerboundJobValidateRequestPacket() ServerPlayer not found: {peer.Id}");
            return;
        }

        //Find the station and validator
        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController) || networkedStationController.JobValidator == null)
        {
            LogWarning($"OnServerboundJobValidateRequestPacket() JobValidator not found. StationNetId: {packet.StationNetId}, StationController found: {networkedStationController != null}, JobValidator found: {networkedStationController?.JobValidator != null}");
            return;
        }

        LogDebug(() => $"OnServerboundJobValidateRequestPacket() Validating {packet.JobNetId}, Validation Type: {packet.validationType}, item: {packet.ItemNetId}");
        switch (packet.validationType)
        {
            case ValidationType.JobOverview:
                if (networkedJob.JobOverview == null ||
                    networkedJob.JobOverview.NetId != packet.ItemNetId)
                {
                    LogWarning($"Job overview {packet.ItemNetId} is not canonical for job {packet.JobNetId}");
                    SendPacket(peer, new ClientboundJobValidateResponsePacket
                        { JobNetId = packet.JobNetId, Invalid = true }, DeliveryMethod.ReliableOrdered);
                    return;
                }
                networkedJob.PendingBookletIssuedToPlayerId = player.PlayerId;
                try
                {
                    networkedStationController.JobValidator.ProcessJobOverview(
                        networkedJob.JobOverview.GetTrackedItem<JobOverview>());
                }
                finally
                {
                    networkedJob.PendingBookletIssuedToPlayerId = 0;
                }
                break;

            case ValidationType.JobBooklet:
                if (!networkedJob.TryGetJobBooklet(packet.ItemNetId, out NetworkedItem booklet))
                {
                    LogWarning($"Job booklet {packet.ItemNetId} is not registered for job {packet.JobNetId}");
                    SendPacket(peer, new ClientboundJobValidateResponsePacket
                        { JobNetId = packet.JobNetId, Invalid = true }, DeliveryMethod.ReliableOrdered);
                    return;
                }
                networkedStationController.JobValidator.ValidateJob(
                    booklet.GetTrackedItem<JobBooklet>());
                break;
        }

        //SendPacket(peer, new ClientboundJobValidateResponsePacket { JobNetId = packet.JobNetId, Invalid = false }, DeliveryMethod.ReliableUnordered);
    }

    private void OnServerboundWarehouseMachineControllerRequestPacket(ServerboundWarehouseMachineControllerRequestPacket packet, ITransportPeer peer)
    {
        LogDebug(() => $"ServerboundWarehouseMachineControllerRequestPacket(): {packet.NetId}");

        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"ServerboundWarehouseMachineControllerRequestPacket() ServerPlayer not found: {peer.Id}");
            return;
        }

        //Todo: add check for player authorisation to use loading/uloading machines

        //Find the warehouse
        if (!NetworkedWarehouseMachineController.Get(packet.NetId, out var targetWarehouse))
        {
            LogWarning($"ServerboundWarehouseMachineControllerRequestPacket() WarehouseMachineController not found. NetId: {packet.NetId}");
            return;
        }

        //Todo: add check for player distance from machine

        targetWarehouse.ServerProcessWarehouseAction(packet.WarehouseAction);
    }

    private void OnCommonChatPacket(CommonChatPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            ChatManager.ProcessMessage(packet.message, player);
    }
    #endregion

    #region Unconnected Packet Handling
    private void OnUnconnectedPingPacket(UnconnectedPingPacket packet, IPEndPoint endPoint)
    {
        //Log($"OnUnconnectedPingPacket({endPoint.Address})");
        //SendUnconnectedPacket(packet, endPoint.Address.ToString(), endPoint.Port);
    }

    private void OnCommonPitStopInteractionPacket(CommonPitStopInteractionPacket packet, ITransportPeer peer)
    {
        bool foundPlayer = TryGetServerPlayer(peer, out var player);
        if (!foundPlayer)
        {
            LogWarning($"Received Pit Stop Plug Interaction, but player was not found");
        }
        else
        {
            if (NetworkedPitStopStation.Get(packet.NetId, out NetworkedPitStopStation controller))
                controller.ProcessInteractionPacketAsHost(packet, player);
            else
                LogWarning($"OnCommonPitStopInteractionPacket() Failed to find PitStopStation with netId: {packet.NetId}");
        }
    }

    private void OnCommonPitStopPlugInteractionPacket(CommonPitStopPlugInteractionPacket packet, ITransportPeer peer)
    {
        bool foundPlayer = TryGetServerPlayer(peer, out var player);
        if (!foundPlayer)
        {
            LogWarning($"Received Pit Stop Plug Interaction, but player was not found");
            SendNetSerializablePacket(peer, new CommonPitStopPlugInteractionPacket
            {
                NetId = packet.NetId,
                InteractionType = (byte)PitStopStationInteractionType.Reject
            }, DeliveryMethod.ReliableOrdered);
        }

        if (NetworkedPluggableObject.Get(packet.NetId, out NetworkedPluggableObject plug) && foundPlayer)
        {
            plug.ProcessInteractionPacketAsHost(packet, player);
        }
        else
        {
            LogError($"OnCommonPitStopInteractionPacket() Failed to find PitStopStation with netId: {packet.NetId}");
        }
    }

    //private void OnCommonItemChangePacket(CommonItemsBulkUpdatePacket packet, ITransportPeer peer)
    //{
    //    if (!TryGetServerPlayer(peer, out var player))
    //        return;

    //    LogDebug(() => $"OnCommonItemChangePacket({packet?.Items?.Count}, {peer.Id} (\"{player.Username}\"))");

    //    LogDebug(() =>
    //    {
    //        string debug = "";

    //        foreach (var item in packet?.Items)
    //        {
    //            debug += "UpdateType: " + item?.UpdateType + "\r\n";
    //            debug += "itemNetId: " + item?.ItemNetId + "\r\n";
    //            debug += "PrefabName: " + item?.PrefabName + "\r\n";
    //            debug += "Equipped: " + item?.ItemState + "\r\n";
    //            debug += "Position: " + item?.ItemPosition + "\r\n";
    //            debug += "Rotation: " + item?.ItemRotation + "\r\n";
    //            debug += "ThrowDirection: " + item?.ThrowDirection + "\r\n";
    //            debug += "Player: " + player.PlayerId + "\r\n";
    //            debug += "CarNetId: " + item?.CarNetId + "\r\n";
    //            debug += "AttachedFront: " + item?.AttachedFront + "\r\n";

    //            debug += "States:";

    //            if (item.States != null)
    //                foreach (var state in item?.States)
    //                    debug += "\r\n\t" + state.Key + ": " + state.Value;
    //        }

    //        return debug;
    //    }

    //    );

    //    // Set player id for all items, do not trust the client to send the correct player id
    //    foreach (var item in packet.Items)
    //        item.Player = player.PlayerId;

    //    NetworkedItemManager.Instance.ReceiveSnapshots(packet.Items);
    //}

    private void OnCommonItemUpdatePacket(CommonItemUpdatePacket packet, ITransportPeer peer)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Server, "Item", packet?.ItemData?.ItemNetId.ToString());
        if (!TryGetServerPlayer(peer, out var player))
        {
            DebugTrace.Validation("item", "Item", packet?.ItemData?.ItemNetId.ToString(), false, "unknown-sender", DebugRuntimeSide.Server);
            return;
        }

        LogDebug(() => $"OnCommonItemUpdatePacket({packet?.ItemData.ItemNetId}, [{peer.Id}, {player.Username}])");
        if (packet?.ItemData == null)
        {
            DebugTrace.Validation("item", "Item", string.Empty, false, "missing-item-data", DebugRuntimeSide.Server);
            return;
        }
        // Runtime creation is an explicit host spawn operation or the narrowly fenced TEMPORARY
        // compatibility-adoption bridge. A normal state packet may never cause a client-authored
        // logical entity to appear on the host.
        if (packet.ItemData.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
        {
            DebugTrace.Validation("item", "Item", packet.ItemData.ItemNetId.ToString(), false,
                "client-create-not-permitted", DebugRuntimeSide.Server);
            return;
        }
        // Set player id for all items, do not trust the client to send the correct player id

        packet.ItemData.PlayerId = player.PlayerId;
        if (NetworkedItem.TryGet(packet.ItemData.ItemNetId, out var networkedItem))
        {
            networkedItem.Server_ReceiveItemUpdate(packet.ItemData, player);
        }
        else
        {
            DebugTrace.Validation("item", "Item", packet.ItemData.ItemNetId.ToString(), false, "unknown-network-entity", DebugRuntimeSide.Server);
        }
    }

    private void OnServerboundJobBookletSummonRequestPacket(
        ServerboundJobBookletSummonRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (!NetworkedStationController.Get(packet.StationNetId,
                out NetworkedStationController station) || station.JobValidator == null)
        {
            LogWarning($"Job booklet summon station not found: {packet.StationNetId}");
            return;
        }
        JobBookletSummonCoordinator.TryStart(station.JobValidator, player);
    }

    private void OnServerboundItemAdoptionPacket(ServerboundItemAdoptionPacket packet, ITransportPeer peer)
    {
        // TEMPORARY: remove this handler after shops, receipts, and every other remaining DV item
        // producer have explicit request/validation/host-create operations.
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Server, "Item", null);
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        ItemAdoptionRequestData request = packet.Request;
        bool accepted = NetworkedItemManager.Instance.TryAdoptClientItem(player, request,
            out ushort assignedNetId, out string rejectionReason, out ItemUpdateData restoredOriginal);
        AuthoritativeItemRegistry.TryGet(assignedNetId, out AuthoritativeItemRegistry.Record authorityRecord);
        DebugRuntime.Publish("item", accepted ? "item.adoption-accepted" : "item.adoption-rejected",
            DebugRuntimeSide.Server, accepted ? DebugSeverity.Info : DebugSeverity.Warning,
            "Item", assignedNetId.ToString(), new()
            {
                ["adoptionToken"] = request.AdoptionToken ?? string.Empty,
                ["prefab"] = request.PrefabName ?? string.Empty,
                ["playerId"] = player.PlayerId,
                ["assignedNetId"] = assignedNetId,
                ["rejectionReason"] = rejectionReason ?? string.Empty,
                ["snapshot"] = DebugTrace.ItemSnapshotData(request.Snapshot)
            });

        SendPacket(peer, new ClientboundItemAdoptionPacket
        {
            Result = new ItemAdoptionResultData
            {
                AdoptionToken = request.AdoptionToken,
                Accepted = accepted,
                AssignedNetId = assignedNetId,
                RejectionReason = rejectionReason ?? string.Empty,
                AuthorityRevision = authorityRecord?.Revision ?? 0,
                PersistentOwnerPlayerId = authorityRecord?.PersistentOwnerPlayerId ?? 0,
                InventoryClaimPlayerId = authorityRecord?.InventoryClaimPlayerId ?? 0,
                InventoryClaimSlot = authorityRecord?.InventoryClaimSlot ?? -1,
                InventoryClaimFlags = authorityRecord?.InventoryClaimFlags ?? ItemInventoryClaimFlags.None
            }
        }, DeliveryMethod.ReliableOrdered);

        // Sent after the rejection on the same ordered channel: the client retires its fresh copy
        // first, freeing the inventory slot the original is restored into.
        if (restoredOriginal != null)
            SendItemUpdatePacket(restoredOriginal, sendToPlayer: player);
    }

    private void OnServerboundItemBelowMapPacket(ServerboundItemBelowMapPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player) &&
            !float.IsNaN(packet.SurfaceHeight) && !float.IsInfinity(packet.SurfaceHeight))
            NetworkedItemManager.Instance?.Server_HandleBelowMapReport(player, packet.ItemNetId, packet.SurfaceHeight);
    }

    private void OnServerboundMoneyItemDepositPacket(ServerboundMoneyItemDepositPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        // Already spent (e.g. two players grabbed the same note): the client's local copy is gone
        // and the next money broadcast corrects its wallet, so there is nothing to do.
        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) || item == null ||
            !item.TryGetComponent(out IMoney money) || !money.ShouldDestroyOnUse)
        {
            LogDebug(() => $"Money deposit from P{player.PlayerId} ignored: netId {packet.ItemNetId} is not live money");
            return;
        }

        if (AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) &&
            (record.Placement is ItemPlacementKind.LostAndFound or ItemPlacementKind.Container ||
             record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
             record.PlacementPlayerId != player.PlayerId))
        {
            LogWarning($"Money deposit from P{player.PlayerId} rejected: netId {item.NetId} is {record.Placement} " +
                $"(player {record.PlacementPlayerId})");
            return;
        }

        double amount = money.TrySpend(money.Amount);
        Log($"P{player.PlayerId} deposited ${amount:N2} from money item {item.NetId}");
        if (amount > double.Epsilon)
            Inventory.Instance.AddMoney(amount); // MoneyChanged broadcasts the new balance
        UnityEngine.Object.Destroy(item.gameObject); // NetworkedItem.OnDestroy replicates the destroy
    }

    // Players given the first-join starter kit this session (ones with saved data never get it).
    internal readonly HashSet<Guid> StarterKitsGiven = new();

    // Well above any basket; refunds are further capped by what the player has paid in.
    private const double MaxShopPayment = 1_000_000;

    private void OnServerboundShopPaymentPacket(ServerboundShopPaymentPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        if (double.IsNaN(packet.Amount) || Math.Abs(packet.Amount) <= double.Epsilon ||
            Math.Abs(packet.Amount) > MaxShopPayment)
        {
            LogWarning($"Shop payment from P{player.PlayerId} rejected: amount {packet.Amount}");
            return;
        }

        if (packet.Amount > 0)
        {
            // The client has already taken it from its copy of the wallet; if the shared wallet is
            // shorter than it thought (a teammate spent meanwhile), take what there is.
            double paid = Math.Min(packet.Amount, Inventory.Instance.PlayerMoney);
            Log($"P{player.PlayerId} paid ${paid:N2} into a shop register");
            if (paid <= double.Epsilon)
                return;
            player.ShopCashInRegisters += paid;
            Inventory.Instance.RemoveMoney(paid); // MoneyChanged broadcasts the new balance
            return;
        }

        // Change or a cancelled basket: never more than this player has put into shop registers.
        double refund = Math.Min(-packet.Amount, player.ShopCashInRegisters);
        Log($"P{player.PlayerId} got ${refund:N2} back from a shop register (asked for ${-packet.Amount:N2})");
        if (refund <= double.Epsilon)
            return;
        player.ShopCashInRegisters -= refund;
        Inventory.Instance.AddMoney(refund);
    }

    // Players buy within a few metres of the till; the margin covers walking off while it sends.
    private const float MaxShopPurchaseDistance = 50f;
    private const double ShopPriceTolerance = 0.5;

    private void OnServerboundShopPurchasePacket(ServerboundShopPurchasePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        GlobalShopController shops = GlobalShopController.Instance;
        Shop shop = shops != null && packet.ShopIndex >= 0 && packet.ShopIndex < shops.globalShopList.Count
            ? shops.globalShopList[packet.ShopIndex]
            : null;
        ShopItemData data = shop != null ? shops.GetShopItemData(packet.ItemPrefabName ?? string.Empty) : null;
        if (data == null || packet.Amount <= 0)
        {
            LogWarning($"Shop purchase from P{player.PlayerId} rejected: shop {packet.ShopIndex}, item '{packet.ItemPrefabName}' x{packet.Amount}");
            SendShopStock(peer);
            return;
        }

        double cost = (double)data.basePrice * packet.Amount;
        string rejection = null;
        if (player.ShopCashInRegisters + ShopPriceTolerance < cost)
            rejection = $"paid ${player.ShopCashInRegisters:N2} of ${cost:N2}";
        else if (data.ItemsInStock < packet.Amount)
            rejection = $"only {data.ItemsInStock} in stock";
        else if ((player.AbsoluteWorldPosition - (shop.itemSpawnTransform.position - WorldMover.currentMove)).sqrMagnitude >
                 MaxShopPurchaseDistance * MaxShopPurchaseDistance)
            rejection = "too far from the shop";

        if (rejection != null)
        {
            LogWarning($"Shop purchase of {packet.ItemPrefabName} x{packet.Amount} from P{player.PlayerId} rejected: {rejection}");
            // The client has already taken its basket's price out of the register; give back what it paid.
            double refund = Math.Min(cost, player.ShopCashInRegisters);
            if (refund > double.Epsilon)
            {
                player.ShopCashInRegisters -= refund;
                Inventory.Instance.AddMoney(refund);
            }
            SendShopStock(peer);
            return;
        }

        player.ShopCashInRegisters = Math.Max(0, player.ShopCashInRegisters - cost);
        Log($"P{player.PlayerId} bought {packet.ItemPrefabName} x{packet.Amount} for ${cost:N2}");
        data.purchasedItems += packet.Amount;
        UnlockablesManager.Instance.UnlockItem(data.item.ItemPrefabName);
        CoroutineManager.Instance.StartCoroutine(SpawnShopPurchase(data.item.ItemPrefabName, shop, packet.Amount, player));
        shops.Fire_GlobalShopDataChanged();
        SendShopStock();
    }

    // As GlobalShopController.InstantiatePurchasedItems, but always at the shop: that one leaves
    // items in the Lost and Found when the host is not near the shop. The buyer owns what they
    // bought; unowned, it went to whoever picked it up first.
    private static System.Collections.IEnumerator SpawnShopPurchase(string itemPrefabName, Shop shop, int amount, ServerPlayer buyer)
    {
        for (int i = 0; i < amount; i++)
        {
            if (shop == null)
                yield break;
            GameObject obj = UnityEngine.Object.Instantiate(Resources.Load(itemPrefabName) as GameObject,
                shop.itemSpawnTransform.position, Quaternion.identity);
            obj.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
            if (obj.TryGetComponent(out ShopRestocker restocker))
                restocker.restockOnItemDestroyed = true;
            ItemBase item = obj.GetComponent<ItemBase>();
            StorageController.Instance.AddItemToWorldStorage(item);
            item.ItemRigidbody.velocity = Vector3.zero;
            item.ItemRigidbody.angularVelocity = Vector3.zero;
            if (obj.TryGetComponent(out APurchaseTrigger trigger))
                trigger.OnPurchased(obj);
            NetworkedItem networked = obj.GetOrAddComponent<NetworkedItem>();
            if (buyer != null && buyer.Guid != Guid.Empty && networked != null && networked.NetId != 0 &&
                AuthoritativeItemRegistry.Ensure(networked) is { PersistentOwnerPlayerId: 0 } record)
            {
                record.PersistentOwnerPlayerId = buyer.PlayerId;
                record.PersistentOwnerIdentity = buyer.Guid;
                buyer.AddOwnedItem(networked.NetId);
            }
            yield return WaitFor.Seconds(0.5f);
        }
    }

    public void SendShopStock(ITransportPeer peer = null)
    {
        GlobalShopController shops = GlobalShopController.Instance;
        if (shops == null)
            return;
        ClientboundShopStockPacket packet = new()
        {
            ItemPrefabNames = shops.shopItemsData.Select(d => d.item.ItemPrefabName).ToArray(),
            PurchasedItems = shops.shopItemsData.Select(d => d.purchasedItems).ToArray()
        };
        if (peer != null)
            SendPacket(peer, packet, DeliveryMethod.ReliableOrdered);
        else
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForWorldState, excludeSelf: true);
    }

    private void OnServerboundItemRecallPacket(ServerboundItemRecallPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"[LostAndFound Recall] Recall packet has no authenticated player: " +
                $"peer={peer?.Id}, netId={packet.ItemNetId}");
            return;
        }

        Log($"[LostAndFound Recall] Recall packet received: player=P{player.PlayerId}, " +
            $"netId={packet.ItemNetId}, revision={packet.ExpectedRevision}, slot={packet.RequestedSlot}");

        bool accepted = false;
        bool storedInLostAndFound = false;
        string rejectionReason = string.Empty;
        ItemUpdateData snapshot = null;
        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) || item == null)
            rejectionReason = "unknown-network-entity";
        else if (storedInLostAndFound = NetworkedLostAndFoundManager.Contains(item.NetId))
            accepted = NetworkedLostAndFoundManager.TryRetrieveForRecall(player, item,
                packet.ExpectedRevision, packet.RequestedSlot, out snapshot, out rejectionReason);
        else if (pendingItemRecallByItem.ContainsKey(item.NetId))
            rejectionReason = "recall-already-pending";
        else if (AuthoritativeItemRegistry.TryPrepareRecall(item, player, packet.RequestedSlot,
                     packet.ExpectedRevision, out snapshot, out rejectionReason))
        {
            uint operationId = NextItemRecallOperationId();
            PendingItemRecall pending = new(operationId, item.NetId, packet.ExpectedRevision,
                packet.RequestedSlot, unchecked(NetworkLifecycle.Instance.Tick + ItemRecallTimeoutTicks),
                player, peer);
            pendingItemRecalls.Add(operationId, pending);
            pendingItemRecallByItem.Add(item.NetId, operationId);
            SendPacket(peer, new ClientboundItemRecallPreparePacket
            {
                OperationId = operationId,
                ItemNetId = item.NetId,
                BaseRevision = packet.ExpectedRevision,
                PreparedRevision = snapshot.AuthorityRevision,
                RequestedSlot = packet.RequestedSlot
            }, DeliveryMethod.ReliableOrdered);
            DebugRuntime.Publish("item", "item.recall-prepare-requested", DebugRuntimeSide.Server,
                entityType: "Item", entityId: item.NetId.ToString(), data: new()
                {
                    ["operationId"] = operationId,
                    ["requestingPlayerId"] = player.PlayerId,
                    ["baseRevision"] = packet.ExpectedRevision,
                    ["preparedRevision"] = snapshot.AuthorityRevision,
                    ["requestedSlot"] = packet.RequestedSlot
                });
            return;
        }

        if (accepted)
        {
            if (!storedInLostAndFound)
                item.ApplyServerCanonicalSnapshot(snapshot);
            SendItemUpdatePacket(snapshot);
        }
        else
        {
            DebugRuntime.Publish("item", "item.recall-rejected", DebugRuntimeSide.Server,
                DebugSeverity.Warning, "Item", packet.ItemNetId.ToString(), new()
                {
                    ["requestingPlayerId"] = player.PlayerId,
                    ["expectedRevision"] = packet.ExpectedRevision,
                    ["requestedSlot"] = packet.RequestedSlot,
                    ["rejectionReason"] = rejectionReason ?? string.Empty
                });
        }

        SendPacket(peer, new ClientboundItemRecallResultPacket
        {
            OperationId = 0,
            ItemNetId = packet.ItemNetId,
            Accepted = accepted,
            AuthorityRevision = snapshot?.AuthorityRevision ?? 0,
            RejectionReason = rejectionReason ?? string.Empty
        }, DeliveryMethod.ReliableOrdered);
        Log($"[LostAndFound Recall] Recall result sent: player=P{player.PlayerId}, " +
            $"netId={packet.ItemNetId}, stored={storedInLostAndFound}, accepted={accepted}, " +
            $"revision={snapshot?.AuthorityRevision ?? 0}, reason={rejectionReason ?? string.Empty}");
    }

    private void OnServerboundItemRecallPreparedPacket(ServerboundItemRecallPreparedPacket packet,
        ITransportPeer peer)
    {
        if (!pendingItemRecalls.TryGetValue(packet.OperationId, out PendingItemRecall pending) ||
            pending.Peer != peer || pending.ItemNetId != packet.ItemNetId)
            return;
        if (!packet.Succeeded)
        {
            CancelPendingItemRecall(pending, string.IsNullOrWhiteSpace(packet.FailureReason)
                ? "client-recall-prepare-failed" : packet.FailureReason, true);
            return;
        }
        string rejectionReason = string.Empty;
        if (!NetworkedItem.TryGet(pending.ItemNetId, out NetworkedItem item) || item == null ||
            !AuthoritativeItemRegistry.TryCommitPreparedRecall(item, pending.Player,
                pending.RequestedSlot, pending.BaseRevision, out ItemUpdateData snapshot,
                out rejectionReason))
        {
            CancelPendingItemRecall(pending, rejectionReason ?? "recall-commit-failed", true);
            return;
        }

        RemovePendingItemRecall(pending);
        item.ApplyServerCanonicalSnapshot(snapshot);
        SendItemUpdatePacket(snapshot);
        SendPacket(peer, new ClientboundItemRecallResultPacket
        {
            OperationId = pending.OperationId,
            ItemNetId = item.NetId,
            Accepted = true,
            AuthorityRevision = snapshot.AuthorityRevision,
            RejectionReason = string.Empty
        }, DeliveryMethod.ReliableOrdered);
    }

    private void CancelPendingItemRecall(PendingItemRecall pending, string reason, bool notifyClient)
    {
        RemovePendingItemRecall(pending);
        uint revision = 0;
        if (NetworkedItem.TryGet(pending.ItemNetId, out NetworkedItem item) && item != null &&
            AuthoritativeItemRegistry.TryCreateCorrectionSnapshot(item, out ItemUpdateData correction))
        {
            revision = correction.AuthorityRevision;
            if (notifyClient)
                SendItemUpdatePacket(correction, pending.Player);
        }
        DebugRuntime.Publish("item", reason == "recall-prepare-timeout"
                ? "item.recall-transaction-timeout" : "item.recall-transaction-cancelled",
            DebugRuntimeSide.Server, DebugSeverity.Warning, "Item", pending.ItemNetId.ToString(), new()
            {
                ["operationId"] = pending.OperationId,
                ["requestingPlayerId"] = pending.Player?.PlayerId ?? 0,
                ["baseRevision"] = pending.BaseRevision,
                ["rejectionReason"] = reason ?? string.Empty
            });
        if (notifyClient && pending.Peer != null)
            SendPacket(pending.Peer, new ClientboundItemRecallResultPacket
            {
                OperationId = pending.OperationId,
                ItemNetId = pending.ItemNetId,
                Accepted = false,
                AuthorityRevision = revision,
                RejectionReason = reason ?? string.Empty
            }, DeliveryMethod.ReliableOrdered);
    }

    private void RemovePendingItemRecall(PendingItemRecall pending)
    {
        pendingItemRecalls.Remove(pending.OperationId);
        pendingItemRecallByItem.Remove(pending.ItemNetId);
    }

    private uint NextItemRecallOperationId()
    {
        do nextItemRecallOperationId++;
        while (nextItemRecallOperationId == 0 || pendingItemRecalls.ContainsKey(nextItemRecallOperationId));
        return nextItemRecallOperationId;
    }

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

    private void OnServerboundLostItemsRequestPacket(ServerboundLostItemsRequestPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;
        SendLostItemsSnapshot(player, packet.RequestId);
    }

    private void OnServerboundLostItemRetrievePacket(ServerboundLostItemRetrievePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
        {
            LogWarning($"[LostAndFound Recall] Retrieval packet has no authenticated player: " +
                $"peer={peer?.Id}, request={packet.RequestId}, handle={packet.LostHandle}");
            return;
        }
        Log($"[LostAndFound Recall] Retrieval packet received: player=P{player.PlayerId}, " +
            $"request={packet.RequestId}, handle={packet.LostHandle}, revision={packet.ExpectedRevision}, " +
            $"requestedSlot={packet.RequestedSlot}, existingSlot={packet.ExistingItemSlot}, " +
            $"capacity={packet.InventoryCapacity}, occupied=[{string.Join(",", packet.OccupiedSlots ?? System.Array.Empty<int>())}]");
        bool accepted = NetworkedLostAndFoundManager.TryRetrieve(player, packet.LostHandle,
            packet.ExpectedRevision, packet.RequestedSlot, packet.ExistingItemSlot, packet.InventoryCapacity,
            packet.OccupiedSlots, out ItemUpdateData snapshot, out string rejectionReason);
        if (accepted)
            SendItemUpdatePacket(snapshot);
        SendPacket(peer, new ClientboundLostItemRetrieveResultPacket
        {
            RequestId = packet.RequestId,
            LostHandle = packet.LostHandle,
            Accepted = accepted,
            AuthorityRevision = snapshot?.AuthorityRevision ?? 0,
            RejectionReason = rejectionReason ?? string.Empty
        }, DeliveryMethod.ReliableOrdered);
        // Successful retrievals publish the changed owner snapshot at the shared commit point.
        // Rejections can still invalidate a stale registry entry, so retain the explicit refresh.
        if (!accepted)
            SendLostItemsSnapshot(player, packet.RequestId);
        Log($"[LostAndFound Recall] Retrieval result sent: player=P{player.PlayerId}, " +
            $"request={packet.RequestId}, handle={packet.LostHandle}, accepted={accepted}, " +
            $"revision={snapshot?.AuthorityRevision ?? 0}, reason={rejectionReason ?? string.Empty}");
    }

    private void OnServerboundContainerBrowsePacket(ServerboundContainerBrowsePacket packet,
        ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player)) return;
        ColdContainerView view;
        ContainerOperationResult result = packet.ShellNetId != 0
            ? NetworkedColdContainerManager.Browse(player, packet.ShellNetId, packet.Offset,
                packet.Count, out view)
            : NetworkedColdContainerManager.Browse(player, packet.ContainerHandle, packet.Offset,
                packet.Count, out view);
        SendPacket(peer, ProjectContainerView(packet.RequestId, result, view),
            DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundContainerMutationPacket(ServerboundContainerMutationPacket packet,
        ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player)) return;
        Guid operationId = packet.OperationId?.Length == 16
            ? new Guid(packet.OperationId) : Guid.Empty;
        ContainerOperationResult result;
        ushort materializedNetId = 0;
        if (operationId == Guid.Empty)
        {
            result = new ContainerOperationResult
            {
                Kind = (ContainerOperationKind)packet.Kind,
                Status = ContainerOperationStatus.InvalidRecord,
                Reason = "operation-id-invalid"
            };
        }
        else
        {
            switch ((ContainerOperationKind)packet.Kind)
            {
                case ContainerOperationKind.Deposit:
                    result = NetworkedColdContainerManager.Deposit(player, operationId,
                        packet.ShellNetId, packet.DestinationContainerHandle,
                        packet.ExpectedDestinationRevision,
                        packet.DestinationSlot, packet.ItemNetId);
                    break;
                case ContainerOperationKind.Withdraw:
                    CoroutineManager.Instance.StartCoroutine(
                        NetworkedColdContainerManager.Withdraw(player, operationId,
                        packet.SourceContainerHandle, packet.ExpectedSourceRevision,
                        packet.SourceSlot, packet.DestinationSlot,
                        (withdrawalResult, withdrawnNetId) =>
                            SendContainerMutationResult(peer, packet, operationId,
                                withdrawalResult, withdrawnNetId)));
                    return;
                case ContainerOperationKind.Move:
                    result = NetworkedColdContainerManager.Move(player, operationId,
                        packet.SourceContainerHandle, packet.ExpectedSourceRevision,
                        packet.SourceSlot, packet.DestinationContainerHandle,
                        packet.ExpectedDestinationRevision, packet.DestinationSlot);
                    break;
                default:
                    result = new ContainerOperationResult
                    {
                        OperationId = operationId,
                        Kind = (ContainerOperationKind)packet.Kind,
                        Status = ContainerOperationStatus.InvalidRecord,
                        Reason = "container-mutation-kind-invalid"
                    };
                    break;
            }
        }
        SendContainerMutationResult(peer, packet, operationId, result, materializedNetId);
    }

    private void SendContainerMutationResult(ITransportPeer peer,
        ServerboundContainerMutationPacket packet, Guid operationId,
        ContainerOperationResult result, ushort materializedNetId)
    {
        SendPacket(peer, new ClientboundContainerMutationResultPacket
        {
            RequestId = packet.RequestId,
            OperationId = operationId.ToByteArray(),
            Kind = (byte)result.Kind,
            Accepted = result.Accepted,
            Status = (byte)result.Status,
            RejectionReason = result.Reason ?? string.Empty,
            SourceRevision = result.SourceRevision,
            DestinationRevision = result.DestinationRevision,
            MaterializedItemNetId = materializedNetId
        }, DeliveryMethod.ReliableOrdered);
    }

    private static ClientboundContainerViewPacket ProjectContainerView(uint requestId,
        ContainerOperationResult result, ColdContainerView view)
    {
        ColdContainerSlotView[] slots = view?.Slots?.ToArray() ?? Array.Empty<ColdContainerSlotView>();
        return new ClientboundContainerViewPacket
        {
            RequestId = requestId,
            Accepted = result.Accepted,
            RejectionReason = result.Reason ?? string.Empty,
            ContainerHandle = view?.ContainerHandle ?? 0,
            Revision = view?.Revision ?? 0,
            Capacity = view?.Capacity ?? 0,
            Offset = view?.Offset ?? 0,
            HasMore = view?.HasMore ?? false,
            Slots = slots.Select(slot => slot.Slot).ToArray(),
            ItemHandles = slots.Select(slot => slot.ItemHandle).ToArray(),
            PrefabNames = slots.Select(slot => slot.PrefabName ?? string.Empty).ToArray(),
            DisplayNames = slots.Select(slot => slot.DisplayName ?? string.Empty).ToArray(),
            ForeignOwned = slots.Select(slot => slot.ForeignOwned).ToArray(),
            ChildContainerHandles = slots.Select(slot => slot.ChildContainerHandle).ToArray(),
            ChildItemCounts = slots.Select(slot => slot.ChildItemCount).ToArray(),
            StateVersions = slots.Select(slot => slot.StateVersion).ToArray()
        };
    }

    private void OnCommonCashRegisterWithModulesActionPacket(CommonCashRegisterWithModulesActionPacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Cash Register With Modules Action received, but player was not found");
            return;
        }

        if (!NetworkedCashRegisterWithModules.Get(packet.NetId, out NetworkedCashRegisterWithModules netCashRegister))
        {
            LogWarning($"Cash Register With Modules Action received for netId: {packet.NetId}, but cash register does not exist!");
            return;
        }

        Log($"Cash Register With Modules Action received for {netCashRegister.GetObjectPath()}, Action: {packet.Action}, Amount: {packet.Amount}");
        netCashRegister.Server_ProcessCashRegisterAction(player, packet);
    }

    private void OnCommonGenericSwitchStatePacket(CommonGenericSwitchStatePacket packet, ITransportPeer peer)
    {
        if (!TryGetServerPlayer(peer, out var player))
        {
            LogWarning($"Received Generic Switch State, but player was not found");
            return;
        }

        if (!NetworkedGenericSwitch.TryGet(packet.NetId, out NetworkedGenericSwitch netSwitch))
        {
            LogWarning($"Received Generic Switch State from \"{player.Username}\" for switch {packet.NetId}, but switch does not exist!");
            return;
        }

        netSwitch.Server_ReceiveSwitchState(packet.IsOn, player);
    }

    #endregion
}
