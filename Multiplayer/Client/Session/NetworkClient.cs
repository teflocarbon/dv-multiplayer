using DV;
using DV.Common;
using DV.Booklets;
using DV.Customization;
using DV.Customization.Paint;
using DV.Damage;
using DV.InventorySystem;
using DV.LocoRestoration;
using DV.Logic.Job;
using DV.MultipleUnit;
using DV.ServicePenalty.UI;
using DV.Shops;
using DV.ThingTypes;
using DV.UI;
using DV.UserManagement;
using DV.Utils;
using DV.WeatherSystem;
using LiteNetLib;
using LiteNetLib.Utils;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.Client.Api;
using Multiplayer.Client.Career;
using Multiplayer.Client.Items;
using Multiplayer.Client.Shops;
using Multiplayer.Client.Players;
using Multiplayer.Client.Trains;
using Multiplayer.Client.UI;
using Multiplayer.Client.World;
using Multiplayer.Shared.Api;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Protocol.Data.Player;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Clientbound.SaveGame;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Train;
using Multiplayer.Shared.Protocol.Serialization;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Transport;
using Multiplayer.Shared.Utils;
using Multiplayer.Shared.Shops;
using Multiplayer.Shared.World;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Core.Shops;
using Multiplayer.Core.World;
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Client.Session;

public partial class NetworkClient : NetworkManager, ILocalPlayerSession, IClientApiSession
{
    /// <summary>
    ///     The client this game runs when it plays on another game's server (a host has none: its
    ///     player is a <see cref="LocalPlayerSession"/>); null when none runs. Set by the session root.
    ///     Code that runs for the host's player too reads <see cref="NetworkLifecycle.LocalSession"/>.
    /// </summary>
    public static NetworkClient Instance { get; internal set; }

    public event Action<ClientboundLostItemRetrieveResultPacket> LostItemRetrieveCompleted;
    public event Action<ClientboundContainerViewPacket> ContainerViewReceived;
    public event Action<ClientboundContainerMutationResultPacket> ContainerMutationCompleted;
    protected override string LogPrefix => "[Client]";
    protected override DebugRuntimeSide DebugSide => DebugRuntimeSide.Client;

    private Action<DisconnectReason, string> onDisconnect;
    private string disconnectMessage;

    private ITransportPeer selfPeer;
    private readonly HashSet<Type> observerBlockedPacketWarnings = new();
    private int lastTrainTopologyCommandFrame = -1;
    private string lastTrainTopologyCommandSignature = string.Empty;

    // Observer mode is enforced at the final outbound boundary. Keeping the policy here
    // means native UI, VR, and mod code cannot accidentally bypass it by invoking a
    // lower-level sender. Synchronization requests, acknowledgements, player tracking,
    // preferences, and chat remain available.
    private static readonly HashSet<Type> ObserverMutationPacketTypes = new()
    {
        typeof(ServerboundTimeAdvancePacket),
        typeof(CommonChangeJunctionPacket),
        typeof(CommonRotateTurntablePacket),
        typeof(CommonCouplerInteractionPacket),
        typeof(ServerboundTrainTopologyCommandPacket),
        typeof(CommonBrakeCylinderReleasePacket),
        typeof(CommonHandbrakePositionPacket),
        typeof(ServerboundAddCoalPacket),
        typeof(ServerboundTenderCoalPacket),
        typeof(ServerboundFireboxIgnitePacket),
        typeof(CommonTrainPortsPacket),
        typeof(CommonTrainFusesPacket),
        typeof(ServerboundTrainDeleteRequestPacket),
        typeof(ServerboundTrainRerailRequestPacket),
        typeof(ServerboundSandboxCargoPacket),
        typeof(ServerboundSandboxCarDamagePacket),
        typeof(ServerboundCargoIgnitePacket),
        typeof(ServerboundTrainSpawnRequestPacket),
        typeof(ServerboundWorkTrainRequestPacket),
        typeof(ServerboundLicensePurchaseRequestPacket),
        typeof(ServerboundPayFeesRequestPacket),
        typeof(ServerboundJobValidateRequestPacket),
        typeof(ServerboundWarehouseMachineControllerRequestPacket),
        typeof(CommonPitStopInteractionPacket),
        typeof(CommonPitStopPlugInteractionPacket),
        typeof(CommonItemUpdatePacket),
        typeof(ServerboundItemAdoptionPacket),
        typeof(ServerboundItemRecallPacket),
        typeof(ServerboundItemRecallPreparedPacket),
        typeof(ServerboundMoneyItemDepositPacket),
        typeof(ServerboundShopScanPacket),
        typeof(ServerboundShopDepositPacket),
        typeof(ServerboundHazmatIgnitePacket),
        typeof(ServerboundShopBuyPacket),
        typeof(ServerboundShopCancelPacket),
        typeof(ServerboundShopScannerAimPacket),
        typeof(ServerboundItemBelowMapPacket),
        typeof(ServerboundContainerMutationPacket),
        typeof(ServerboundLostItemRetrievePacket),
        typeof(ServerboundItemSpatialSamplePacket),
        typeof(ServerboundItemSpatialSettlementPacket),
        typeof(ServerboundItemTrainWakeWitnessPacket),
        typeof(ServerboundJobBookletSummonRequestPacket),
        typeof(ServerboundJobRerollRequestPacket),
        typeof(CommonPaintThemePacket),
        typeof(CommonCashRegisterWithModulesActionPacket),
        typeof(ServerboundTrainControlAuthorityPacket),
        typeof(CommonGenericSwitchStatePacket)
    };
    public byte PlayerId { get; private set; }
    public string Username { get; private set; }
    private string characterModelId;
    public string CrewName { get; private set; }
    public string DisplayName => string.IsNullOrEmpty(CrewName) ? Username : $"[{CrewName}] {Username}";

    // The other players' avatars, which this client fills from the server's packets.
    private static PlayerAvatars Avatars => NetworkLifecycle.Instance.Avatars;
    IReadOnlyCollection<NetworkedPlayer> ILocalPlayerSession.RemotePlayers => Avatars.Players;
    bool ILocalPlayerSession.TryGetRemotePlayer(byte playerId, out NetworkedPlayer player) => Avatars.TryGetPlayer(playerId, out player);
    event Action<NetworkedPlayer> ILocalPlayerSession.RemotePlayerConnected
    {
        add => Avatars.OnPlayerConnected += value;
        remove => Avatars.OnPlayerConnected -= value;
    }
    event Action<NetworkedPlayer> ILocalPlayerSession.RemotePlayerDisconnected
    {
        add => Avatars.OnPlayerDisconnected += value;
        remove => Avatars.OnPlayerDisconnected -= value;
    }
    event Action<NetworkedPlayer> ILocalPlayerSession.RemotePlayerPrefsUpdated
    {
        add => Avatars.OnPlayerPrefsUpdated += value;
        remove => Avatars.OnPlayerPrefsUpdated -= value;
    }

    internal PlayerLoadingState LoadingState { get; set; } = PlayerLoadingState.None;
    internal uint trainSetsToSpawn = uint.MaxValue;
    internal uint trainSetsSpawned = 0;
    private readonly TrainBaselineReceipt trainBaseline = new();
    private readonly TrainTopologyReconciler trainTopology = new();
    private bool trainTopologyRetryRunning;

    // Why the host took couplings apart (sent with its snapshots), for this client's own uncouple
    // of the same coupling to record; and, while a refusal of this client's own command is being
    // applied, why it was refused (see CouplerUncoupleLogPatch).
    private readonly Dictionary<(ushort CarNetId, TrainEnd End), ChangeCause<UncoupleReason>> hostUncoupleCauses = new();
    internal ChangeCause<UncoupleReason>? RefusalUncoupleCause { get; private set; }
    private ulong nextTrainTopologyOperationId;
    private float nextTrainTopologySyncRequestAt;
    private const double MaxWeatherClockLeadSeconds = 300; // game time
    private float nextDeferredBrakeTopologyWarningAt;
    private uint lastDeferredBrakeTopologyRevision;
    private ulong nextTrainControlOperationId;
    internal bool railwayStateLoaded = false;
    private Action worldLoadingFinishedHandler;

    // One way ping in milliseconds
    public int Ping { get; private set; }
    private ITransportPeer serverPeer;
    public float RPC_Timeout => (Ping * 8f) / 1000;
    float ILocalPlayerSession.RpcTimeout => RPC_Timeout;

    private ChatGUI chatGUI;
    private readonly bool isSinglePlayer;

    IGameSession originalSession;
    // Proves this game's Steam account to the server; Steam keeps it valid until cancelled.
    private Steamworks.AuthTicket authTicket;

    // Allow mods to add to the wait Queue
    private readonly List<string> readyBlocks = [];
    private readonly Dictionary<ushort, JobReportArtifactData> pendingJobReportArtifacts = new();

    public NetworkClient(Settings settings, bool singlePlayer) : base(settings)
    {
        Log($"Client created for {(singlePlayer ? "single player" : "multiplayer")} game");
        isSinglePlayer = singlePlayer;
        Username = Multiplayer.Settings.GetUserName();
        characterModelId = Multiplayer.Settings.CharacterId;

        Settings.OnSettingsUpdated += OnSettingsUpdated;
    }

    public void Start(string address, int port, string password, bool isSinglePlayer, Action<DisconnectReason, string> onDisconnect)
    {
        LogDebug(() => $"NetworkClient Constructor");

        this.onDisconnect = onDisconnect;
        //netManager.Start();
        base.Start();

        ulong steamId = PlayerIdentity.LocalSteamId;
        byte[] ticket = [];
        if (steamId != 0)
        {
            try
            {
                authTicket = Steamworks.SteamUser.GetAuthSessionTicket(default);
                ticket = authTicket?.Data ?? [];
            }
            catch (Exception e)
            {
                LogWarning($"Couldn't get a Steam session ticket: {e.Message}");
            }
            if (ticket.Length == 0)
                LogWarning("No Steam session ticket: a host that verifies players will refuse this login");
        }

        ServerboundClientLoginPacket serverboundClientLoginPacket = new()
        {
            Username = this.Username,
            Guid = Multiplayer.Settings.GetGuid().ToByteArray(),
            SteamId = ticket.Length > 0 ? steamId : 0,
            AuthTicket = ticket,
            Password = password,
            BuildVersion = FrontEnd.BuildVersion,
            Mods = ModCompatibilityManager.Instance.GetLocalMods(),
            CharacterId = Multiplayer.Settings.CharacterId,
            IsVR = VRManager.IsVREnabled()
        };

        Log("Sending Login Packet");
        // Its own writer: the transport holds on to it until the connection completes, and the
        // shared cachedWriter is reset by every other send in the meantime.
        NetDataWriter loginWriter = new();
        netPacketProcessor.Write(loginWriter, serverboundClientLoginPacket);
        selfPeer = Connect(address, port, loginWriter);

        originalSession = UserManager.Instance.CurrentUser.CurrentSession;

        LogDebug(() => $"NetworkClient.Start() Original session is Null: {originalSession == null}");
    }

    public override void Stop()
    {
        Log("Stopping client");
        ShopShelfLayout.Reset();
        ShopRegisterView.Reset();
        ShopScannerView.Reset();
        ShopRegisterPatches.Reset();
        trainsetRelocationRevisions.Clear();
        pendingTrainsetRelocationRevisions.Clear();
        if (originalSession != null)
        {
            LogDebug(() => $"NetworkClient.Stop() destroying session... Original session is Null: {originalSession == null}");
            Client_GameSession.SetCurrent(originalSession);
        }

        Settings.OnSettingsUpdated -= OnSettingsUpdated;
        authTicket?.Cancel();
        authTicket = null;
        if (worldLoadingFinishedHandler != null)
            WorldStreamingInit.LoadingFinished -= worldLoadingFinishedHandler;
        worldLoadingFinishedHandler = null;

        base.Stop();
    }

    private void OnSettingsUpdated(Settings settings)
    {
        Dictionary<PlayerPreference, string> preferences = [];

        if (settings.CharacterId != characterModelId)
        {
            characterModelId = settings.CharacterId;
            preferences[PlayerPreference.CharacterModel] = characterModelId;
        }

        if (preferences.Count > 0)
            SendPlayerPreferences(preferences);
    }

    protected override void Subscribe()
    {
        // Login, Connection & Initial Sync
        Handle<ClientboundServerLoadingPacket>(OnClientboundServerLoadingPacket);
        Handle<ClientboundLoginResponsePacket>(OnClientboundLoginResponsePacket);
        Handle<ClientboundDisconnectPacket>(OnClientboundDisconnectPacket);
        Handle<ClientboundPingUpdatePacket>(OnClientboundPingUpdatePacket);
        Handle<ClientboundLoadStateInfoPacket>(OnClientboundLoadStateInfoPacket);
        Handle<ClientboundRemoveLoadingScreenPacket>(OnClientboundRemoveLoadingScreen);

        Handle<ClientboundGameParamsPacket>(OnClientboundGameParamsPacket);
        Handle<ClientboundSaveGameDataPacket>(OnClientboundSaveGameDataPacket);
        Handle<ClientboundRailwayStatePacket>(OnClientboundRailwayStatePacket);
        Handle<ClientboundSleepStatePacket>(OnClientboundSleepStatePacket);
        Handle<ClientboundHazmatTilesPacket>(OnClientboundHazmatTilesPacket);
        Handle<ClientboundShopStockPacket>(OnClientboundShopStockPacket);
        Handle<ClientboundShopLayoutPacket>(OnClientboundShopLayoutPacket);
        Handle<ClientboundShopRegisterStatePacket>(OnClientboundShopRegisterStatePacket);
        Handle<ClientboundShopRequestResultPacket>(OnClientboundShopRequestResultPacket);
        Handle<ClientboundShopScannerAimPacket>(OnClientboundShopScannerAimPacket);
        Handle<ClientboundCustomizationsPacket>(OnClientboundCustomizationsPacket);
        Handle<ClientboundGadgetJoinSnapshotPacket>(OnClientboundGadgetJoinSnapshotPacket);


        // General Sync
        HandleNetSerializable<ClientboundRpcResponsePacket>(OnClientboundRpcResponsePacket);
        Handle<ClientboundTickSyncPacket>(OnClientboundTickSyncPacket);
        Handle<ClientboundWeatherPacket>(OnClientboundWeatherPacket);
        Handle<ClientboundTimeAdvancePacket>(OnClientboundTimeAdvancePacket);
        Handle<CommonChangeJunctionPacket>(OnCommonChangeJunctionPacket);
        Handle<CommonRotateTurntablePacket>(OnCommonRotateTurntablePacket);


        // Player Management
        Handle<ClientboundPlayerJoinedPacket>(OnClientboundPlayerJoinedPacket);
        Handle<ClientboundPlayerDisconnectPacket>(OnClientboundPlayerDisconnectPacket);

        Handle<ClientboundPlayerPositionPacket>(OnClientboundPlayerPositionPacket);
        Handle<ClientboundPlayerPreferencesUpdatePacket>(OnClientboundPlayerPreferencesUpdatePacket);


        // Train Sync
        Handle<ClientboundSpawnTrainSetPacket>(OnClientboundSpawnTrainSetPacket);
        Handle<ClientboundTrainBaselineBeginPacket>(OnClientboundTrainBaselineBeginPacket);
        Handle<ClientboundTrainBaselineCompletePacket>(OnClientboundTrainBaselineCompletePacket);
        Handle<ClientboundTrainTopologySnapshotPacket>(OnClientboundTrainTopologySnapshotPacket);
        Handle<ClientboundDestroyTrainCarPacket>(OnClientboundDestroyTrainCarPacket);
        Handle<ClientboundTrainDerailCausePacket>(OnClientboundTrainDerailCausePacket);
        Handle<ClientboundRerailTrainPacket>(OnClientboundRerailTrainPacket);
        Handle<ClientboundMoveTrainPacket>(OnClientboundMoveTrainPacket);
        Handle<ClientboundTrainsetRelocationPacket>(OnClientboundTrainsetRelocationPacket);

        Handle<ClientboundTrainsetPhysicsPacket>(OnClientboundTrainPhysicsPacket);
        Handle<ClientboundBrakeStateUpdatePacket>(OnClientboundBrakeStateUpdatePacket);
        Handle<ClientboundTrainControlCommitPacket>(OnClientboundTrainControlCommitPacket);

        Handle<CommonCouplerInteractionPacket>(OnCommonCouplerInteractionPacket);


        Handle<CommonPaintThemePacket>(OnCommonPaintThemePacket);
        Handle<ClientboundRestorationStateChangePacket>(OnClientboundRestorationStateChangePacket);

        Handle<ClientboundTrainControlAuthorityUpdatePacket>(OnClientboundTrainControlAuthorityUpdatePacket);

        Handle<ClientboundCargoStatePacket>(OnClientboundCargoStatePacket);
        Handle<ClientboundExplosionPacket>(OnClientboundExplosionPacket);
        Handle<ClientboundCargoFirePacket>(OnClientboundCargoFirePacket);
        Handle<ClientboundCargoHealthUpdatePacket>(OnClientboundCargoHealthUpdatePacket);
        Handle<ClientboundCarHealthUpdatePacket>(OnClientboundCarHealthUpdatePacket);
        Handle<ClientboundWarehouseControllerUpdatePacket>(OnClientboundWarehouseControllerUpdatePacket);

        Handle<ClientboundWindowsBrokenPacket>(OnClientboundWindowsBrokenPacket);
        Handle<ClientboundWindowsRepairedPacket>(OnClientboundWindowsRepairedPacket);
        Handle<ClientboundMoneyPacket>(OnClientboundMoneyPacket);
        Handle<ClientboundLicenseAcquiredPacket>(OnClientboundLicenseAcquiredPacket);
        Handle<ClientboundGarageUnlockPacket>(OnClientboundGarageUnlockPacket);

        // Job Sync
        Handle<ClientboundDebtStatusPacket>(OnClientboundDebtStatusPacket);
        Handle<ClientboundJobsUpdatePacket>(OnClientboundJobsUpdatePacket);
        Handle<ClientboundJobsCreatePacket>(OnClientboundJobsCreatePacket);
        Handle<ClientboundJobValidateResponsePacket>(OnClientboundJobValidateResponsePacket);
        Handle<ClientboundJobReportArtifactPacket>(OnClientboundJobReportArtifactPacket);
        Handle<ClientboundTaskUpdatePacket>(OnClientboundTaskUpdatePacket);

        // World Sync
        Handle<CommonItemsBulkUpdatePacket>(OnCommonItemsBulkUpdatePacket);
        Handle<CommonItemUpdatePacket>(OnCommonItemUpdatePacket);
        Handle<ClientboundItemAdoptionPacket>(OnClientboundItemAdoptionPacket);
        Handle<ClientboundItemRecallPreparePacket>(OnClientboundItemRecallPreparePacket);
        Handle<ClientboundItemRecallResultPacket>(OnClientboundItemRecallResultPacket);
        Handle<ClientboundLostItemsSnapshotPacket>(OnClientboundLostItemsSnapshotPacket);
        Handle<ClientboundLostItemRetrieveResultPacket>(OnClientboundLostItemRetrieveResultPacket);
        Handle<ClientboundContainerViewPacket>(OnClientboundContainerViewPacket);
        Handle<ClientboundContainerMutationResultPacket>(OnClientboundContainerMutationResultPacket);
        Handle<ClientboundWorldItemCataloguePacket>(OnClientboundWorldItemCataloguePacket);
        Handle<ClientboundItemSpatialLeasePacket>(OnClientboundItemSpatialLeasePacket);
        Handle<ClientboundItemSpatialSamplePacket>(OnClientboundItemSpatialSamplePacket);
        Handle<ClientboundItemSpatialCommitPacket>(OnClientboundItemSpatialCommitPacket);
        Handle<CommonPitStopInteractionPacket>(OnCommonPitStopInteractionPacket);
        HandleNetSerializable<CommonPitStopPlugInteractionPacket>(OnCommonPitStopPlugInteractionPacket);
        Handle<ClientboundPitStopBulkUpdatePacket>(OnClientboundPitStopBulkUpdatePacket);
        Handle<CommonCashRegisterWithModulesActionPacket>(OnCommonCashRegisterWithModulesActionPacket);
        Handle<CommonGenericSwitchStatePacket>(OnCommonGenericSwitchStatePacket);

        Handle<CommonChatPacket>(OnCommonChatPacket);
    }

    private void Handle<T>(Action<T> onReceive) where T : class, new() =>
        netPacketProcessor.SubscribeReusable(onReceive);

    private void HandleNetSerializable<T>(Action<T> onReceive) where T : INetSerializable, new() =>
        netPacketProcessor.SubscribeNetSerializable(onReceive);

    // Allow mods to register their own packets
    public void RegisterExternalPacket<T>(ClientPacketHandler<T> handler) where T : class, IPacket, new()
    {
        netPacketProcessor.SubscribeReusable<T>((packet) =>
        {
            handler(packet);
        });
    }

    public void RegisterExternalSerializablePacket<T>(ClientPacketHandler<T> handler) where T : class, ISerializablePacket, new()
    {
        netPacketProcessor.SubscribeNetSerializable<ExternalSerializablePacketWrapper<T>>((wrapper) =>
        {
            handler(wrapper.Packet);
        },
        () => new ExternalSerializablePacketWrapper<T>()
        );
    }

    // Allow mods to register ready blocks
    public void RegisterReadyBlock(string modName)
    {
        Log($"Ready Block has been registered by {modName}");

        if (readyBlocks.Contains(modName))
            return;

        readyBlocks.Add(modName);
    }

    public void CancelReadyBlock(string modName)
    {
        Log($"Ready Block has been cleared by {modName}");

        if (readyBlocks.Contains(modName))
        {
            readyBlocks.Remove(modName);
            DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
            displayLoadingInfo?.OnLoadingStatusChanged($"Mod {modName} loaded", false, 100);
        }
    }

    private const float ReadyBlockTimeoutSeconds = 60f;
    private const float RailwayStateTimeoutSeconds = 30f;
    private const float GadgetSnapshotTimeoutSeconds = 30f;

    // Runs a loading coroutine so an exception ends the join with a message. Unity otherwise just
    // stops a coroutine that throws, leaving the player on the loading screen with a half-synced
    // world and nothing to say why.
    private IEnumerator Guarded(IEnumerator routine, string what)
    {
        while (true)
        {
            object current;
            try
            {
                if (!routine.MoveNext())
                    yield break;
                current = routine.Current;
            }
            catch (Exception e)
            {
                FailJoin($"Joining failed during {what}: {e.Message}", e);
                yield break;
            }
            yield return current;
        }
    }

    // The host passes no callback, and a rejected login reached it twice (on the rejection and
    // again on the disconnect that follows); call it at most once.
    private void InvokeOnDisconnect(DisconnectReason reason, string message)
    {
        Action<DisconnectReason, string> callback = onDisconnect;
        onDisconnect = null;
        callback?.Invoke(reason, message);
    }

    private void FailJoin(string message, Exception exception = null)
    {
        disconnectMessage = message;
        LogError(exception == null ? message : $"{message}\n{exception}");
        serverPeer?.Disconnect();
    }

    private IEnumerator SyncWorldState()
    {
        /*
         * This coroutine must not be started prior to WorldStreamingInit.LoadingFinished, otherwise it will be killed
         * Both GameSettings and SaveGameData have been applied at this point and the current loading state is PlayerLoadingState.ReadyForGameData
         */

        Log($"World loaded beginning sync");

        Log($"Starting Item Manager...");
        NetworkedItemManager.Instance.CheckInstance();
        Log($"Quarantining unbound client items...");
        ClientItemManager.Instance?.QuarantineUnboundClientItems();
        SendWorldItemCatalogue();
        InitialiseStations();

        // Wait for ready blocks to clear
        Log($"Waiting for ready-blocks...");
        DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
        foreach (string modName in readyBlocks)
            displayLoadingInfo?.OnLoadingStatusChanged($"Waiting for mod {modName} to load", false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);

        float readyBlockDeadline = Time.realtimeSinceStartup + ReadyBlockTimeoutSeconds;
        while (readyBlocks.Count > 0 && Time.realtimeSinceStartup < readyBlockDeadline)
            yield return null;
        if (readyBlocks.Count > 0)
        {
            // A mod that never reports ready shouldn't strand the player on the loading screen.
            LogWarning($"Mods still not ready after {ReadyBlockTimeoutSeconds:0}s, continuing anyway: {string.Join(", ", readyBlocks)}");
            readyBlocks.Clear();
        }

        /* 
         * ReadyForWorldState
         * Request world state data (tracks and turntables)
         */

        Log("Syncing world state");
        // The host checks it against its own before sending any world state. Taken after the
        // ready-blocks, so a mod that adds track has done so.
        TrackGraphFingerprint trackGraph = TrackGraph.Compute();
        Log($"Track graph: {trackGraph}");
        ShopCatalogueFingerprint shops = ShopCatalogue.Compute();
        Log($"Shops: {shops}");
        SendLoadStateUpdate(PlayerLoadingState.ReadyForWorldState, trackGraph, shops);
        displayLoadingInfo.OnLoadingStatusChanged(Locale.LOADING_INFO__SYNC_WORLD_STATE, false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);

        float railwayDeadline = Time.realtimeSinceStartup + RailwayStateTimeoutSeconds;
        while (!railwayStateLoaded && Time.realtimeSinceStartup < railwayDeadline)
            yield return null;
        if (!railwayStateLoaded)
        {
            FailJoin($"Timed out after {RailwayStateTimeoutSeconds:0}s waiting for the railway state (junctions and turntables)");
            yield break;
        }

        /*
         * ReadyForTrainSets
         * Trainsets have been requested
         */

        Log("Requesting cars");
        trainBaseline.Reset();
        trainSetsToSpawn = uint.MaxValue;
        trainSetsSpawned = 0;
        SendLoadStateUpdate(PlayerLoadingState.ReadyForTrainSets);
        displayLoadingInfo.OnLoadingStatusChanged("Syncing rolling stock", false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);

        float baselineDeadline = Time.realtimeSinceStartup + 45f;
        while (!trainBaseline.Started && string.IsNullOrEmpty(trainBaseline.Failure) &&
               Time.realtimeSinceStartup < baselineDeadline)
            yield return null;

        while (trainBaseline.Started &&
               (!trainBaseline.Complete || !trainTopology.BaselineApplied) &&
               string.IsNullOrEmpty(trainBaseline.Failure) &&
               Time.realtimeSinceStartup < baselineDeadline)
        {
            if (trainBaseline.Complete && trainTopology.HasPending &&
                !trainTopologyRetryRunning)
                StartTrainTopologyRetry();
            yield return null;
        }

        if (!trainBaseline.Complete || !trainTopology.BaselineApplied)
        {
            string reason = string.IsNullOrEmpty(trainBaseline.Failure)
                ? !trainBaseline.Complete
                    ? $"timeout ({trainSetsSpawned}/{trainSetsToSpawn} trainsets)"
                    : "canonical topology readiness timeout"
                : trainBaseline.Failure;
            disconnectMessage = $"Rolling-stock synchronization failed: {reason}";
            LogError(disconnectMessage);
            serverPeer?.Disconnect();
            yield break;
        }

        Log($"Applied rolling-stock baseline {trainBaseline.BaselineId}: " +
            $"{trainBaseline.AppliedTrainsets} trainsets, {trainBaseline.AppliedCars} cars");

        // Artificial delay to allow cargo to be loaded prior to applying restoration states
        yield return new WaitForSeconds(0.5f);

        // Trainsets spawned, apply restoration states for demonstrators
        NetworkedCarSpawner.ApplyRestorationStates();

        /*
         * ReadyForCustomizers
         * Every car exists: the gadgets installed on them (gadgets.md, "Late joiners")
         */

        gadgetJoinApplied = false;
        SendLoadStateUpdate(PlayerLoadingState.ReadyForCustomizers);
        float gadgetDeadline = Time.realtimeSinceStartup + GadgetSnapshotTimeoutSeconds;
        while (!gadgetJoinApplied && Time.realtimeSinceStartup < gadgetDeadline)
            yield return null;
        // Gadgets missing is a worse game, not a broken one: carry on.
        if (!gadgetJoinApplied)
            LogWarning($"No installed gadgets after {GadgetSnapshotTimeoutSeconds:0}s; continuing without them");

        /* 
         * ReadyForItems
         */

        Log($"Train sets spawned, requesting items");
        SendLoadStateUpdate(PlayerLoadingState.ReadyForItems);

        yield return new WaitForSeconds(0.25f);

        /* 
         * ReadyForJobs
         */

        Log($"Requesting jobs");
        SendLoadStateUpdate(PlayerLoadingState.ReadyForJobs);

        yield return new WaitForSeconds(0.25f);


        /* 
         * ReadyForTiles
         */

        Log($"Requesting Hazmat Tiles");
        SendLoadStateUpdate(PlayerLoadingState.ReadyForTiles);

        yield return new WaitForSeconds(0.5f);

        SendLoadStateUpdate(PlayerLoadingState.Complete);
        displayLoadingInfo.OnLoadingStatusChanged("Complete", false, ((float)LoadingState / (float)PlayerLoadingState.Complete) * 100);
        yield return new WaitForSeconds(0.25f);
    }


    public override void OnPeerConnected(ITransportPeer peer)
    {
        serverPeer = peer;
    }

    public override void OnPeerDisconnected(ITransportPeer peer, DisconnectReason disconnectReason)
    {

        LogDebug(() => $"OnPeerDisconnected({peer.Id}, {disconnectReason}) disconnect message: {disconnectMessage}");

        MultiplayerSession.Instance.Stop();

        TrainStress.globalIgnoreStressCalculation = false;

        FrontEnd.ReturnToMainMenu();

        LogDebug(() => $"OnPeerDisconnected() calling onDisconnect({disconnectReason}, {disconnectMessage})");
        InvokeOnDisconnect(disconnectReason, disconnectMessage);
    }

    public override void OnNetworkLatencyUpdate(ITransportPeer peer, int latency)
    {
        Ping = latency;
        DebugDiagnostics.RecordLatency(DebugRuntimeSide.Client, peer?.Id ?? -1, latency);

        if (latency > LATENCY_FLAG)
            LogWarning($"High Ping Detected! {latency}ms");
    }

    public override void OnConnectionRequest(NetDataReader dataReader, IConnectionRequest request)
    {
        // Clients don't receive incomming requests.
        request.Reject();
    }



    private void OnClientboundLoginResponsePacket(ClientboundLoginResponsePacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client);
        if (packet.Accepted)
        {
            string localFingerprint = ProtocolManifestProvider.Current.Fingerprint;
            if (string.IsNullOrWhiteSpace(packet.ProtocolFingerprint) ||
                !string.Equals(packet.ProtocolFingerprint, localFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                disconnectMessage =
                    "Multiplayer protocol mismatch. The host and client must use the exact same Multiplayer build.";
                LogError($"Rejected accepted session due to protocol mismatch. " +
                         $"Host: '{packet.ProtocolFingerprint}', client: '{localFingerprint}'");
                serverPeer?.Disconnect();
                return;
            }

            Log($"Player accepted");
            PlayerId = packet.PlayerId;

            if (!string.IsNullOrEmpty(packet.OverrideUsername))
            {
                Log($"A player with username '{Username}' already exists, your temporary username is '{packet.OverrideUsername}'");
                Username = packet.OverrideUsername;
            }

            DebugRuntime.SetRole("client", Username, PlayerId);

            // Request Game Params and Save Game Data
            SendLoadStateUpdate(PlayerLoadingState.ReadyForGameData);

            // LoadingFinished is static and outlives this client. The handler used to be an
            // anonymous lambda that was never removed, so on a rejoin every earlier session's
            // handler also fired and ran its own (stale) world sync. Subscribe once, remove it when
            // it fires or when this client stops.
            if (worldLoadingFinishedHandler != null)
                WorldStreamingInit.LoadingFinished -= worldLoadingFinishedHandler;
            worldLoadingFinishedHandler = () =>
            {
                WorldStreamingInit.LoadingFinished -= worldLoadingFinishedHandler;
                worldLoadingFinishedHandler = null;
                LogDebug(() => "Loading finished, beginning sync");
                CoroutineManager.Instance.StartCoroutine(Guarded(SyncWorldState(), "world sync"));

                if (WorldMover.Instance.playerTracker != null)
                {
                    var tracker = WorldMover.Instance.playerTracker.GetComponent<LocalPlayerTrackerBase>();
                    if (tracker == null)
                    {
                        LogWarning($"LocalPlayerTracker not found, adding {(VRManager.IsVREnabled() ? "" : "non")}VR tracker.");
                        if (VRManager.IsVREnabled())
                            tracker = WorldMover.Instance.playerTracker.gameObject.AddComponent<LocalPlayerTrackerVR>();
                        else
                            tracker = WorldMover.Instance.playerTracker.gameObject.AddComponent<LocalPlayerTrackerNonVR>();
                    }
                }
            };
            WorldStreamingInit.LoadingFinished += worldLoadingFinishedHandler;

            return;
        }

        string text = Locale.Get(packet.ReasonKey, packet.ReasonArgs);

        if (packet.Missing.Length != 0 || packet.Extra.Length != 0)
        {
            text += "\n\n";

            if (packet.Missing.Length != 0)
            {
                text += Locale.Get(Locale.DISCONN_REASON__MODS_MISSING_KEY, placeholders: string.Join("\n - ", packet.Missing));
            }

            if (packet.Extra.Length != 0)
            {
                if (packet.Missing.Length != 0)
                    text += "\n";
                text += Locale.Get(Locale.DISCONN_REASON__MODS_EXTRA_KEY, placeholders: string.Join("\n - ", packet.Extra));
            }
        }

        Log($"Player denied: {text}");
        InvokeOnDisconnect(DisconnectReason.ConnectionRejected, text);
    }

    private void OnClientboundLoadStateInfoPacket(ClientboundLoadStateInfoPacket packet)
    {
        Log($"Received load state info for loading state: {packet.LoadingState}, item count: {packet.ItemsToLoad}");
        switch (packet.LoadingState)
        {
            case PlayerLoadingState.ReadyForTrainSets:
                trainSetsToSpawn = packet.ItemsToLoad;
                break;
            default:
                LogWarning($"Unexpected loading state: {packet.LoadingState}");
                break;
        }
    }

    private void OnClientboundRpcResponsePacket(ClientboundRpcResponsePacket packet)
    {
        LogDebug(() => $"Received RPC response for ticket: {packet.TicketId}, response type: {packet.ResponseType}");
        RpcManager.Instance.ResolveTicket(packet.TicketId, packet.Response);
    }

    private void OnClientboundPlayerJoinedPacket(ClientboundPlayerJoinedPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Player", packet.PlayerId.ToString());
        Log($"Received player joined packet for player id: {packet.PlayerId}, username: {packet.Username}");
        Avatars.AddPlayer(packet.PlayerId, packet.Username, packet.CrewName, packet.CharacterId, packet.IsVR);

        Avatars.UpdatePosition(packet.PlayerId, packet.TrackingData, packet.Posture, packet.IsOnCar, packet.CarID);
    }

    //For other player left the game
    private void OnClientboundPlayerDisconnectPacket(ClientboundPlayerDisconnectPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Player", packet.PlayerId.ToString());
        Log($"Received player disconnect packet for player id: {packet.PlayerId}");
        Avatars.RemovePlayer(packet.PlayerId);
    }

    //For server shutting down / player kicked
    private void OnClientboundDisconnectPacket(ClientboundDisconnectPacket packet)
    {
        if (packet.Kicked)
        {
            Log($"Player was kicked! {packet.Reason}");
            disconnectMessage = string.IsNullOrEmpty(packet.Reason) ? "You were kicked!" : packet.Reason;
        }
        else
        {
            Log($"Server Shutting Down");
            disconnectMessage = "Server Shutting Down";
        }
    }

    private void OnClientboundPlayerPositionPacket(ClientboundPlayerPositionPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Player", packet.PlayerId.ToString());
        Avatars.UpdatePosition(packet.PlayerId, packet.TrackingData, packet.Posture, packet.IsOnCar, packet.CarID);
    }

    private void OnClientboundPlayerPreferencesUpdatePacket(ClientboundPlayerPreferencesUpdatePacket packet)
    {
        Log($"Received player preferences update for '{packet.PlayerId}'");

        if (packet.PlayerId == PlayerId)
        {
            if (packet.GetPreferencesDictionary().TryGetValue(PlayerPreference.CrewName, out string crewName))
                CrewName = crewName;
        }

        Avatars.UpdatePreferences(packet.PlayerId, packet.GetPreferencesDictionary());
    }

    private void OnClientboundPingUpdatePacket(ClientboundPingUpdatePacket packet)
    {
        Avatars.UpdatePing(packet.PlayerId, packet.Ping);
    }

    private void OnClientboundTickSyncPacket(ClientboundTickSyncPacket packet)
    {
        // Ping is one-way latency in ms; advance the server tick by that much travel time.
        NetworkLifecycle.Instance.Tick = (uint)(packet.ServerTick + Mathf.RoundToInt(Ping / 1000f * NetworkLifecycle.TICK_RATE));
    }

    private void OnClientboundServerLoadingPacket(ClientboundServerLoadingPacket packet)
    {
        Log("Waiting for server to load");

        DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
        if (displayLoadingInfo == null)
        {
            LogDebug(() => $"Received {nameof(ClientboundServerLoadingPacket)} but couldn't find {nameof(DisplayLoadingInfo)}!");
            return;
        }

        displayLoadingInfo.OnLoadingStatusChanged(Locale.LOADING_INFO__WAIT_FOR_SERVER, false, 100);
    }

    private void OnClientboundSaveGameDataPacket(ClientboundSaveGameDataPacket packet)
    {
        if (WorldStreamingInit.isLoaded)
        {
            LogWarning("Received save game data packet while already in game!");
            return;
        }

        Log("Received save game data, loading world");

        AStartGameData.DestroyAllInstances();

        GameObject go = new("Server Start Game Data");

        //create a new save and load it
        go.AddComponent<StartGameData_ServerSave>().SetFromPacket(packet);

        //ensure save is not destroyed on scene switch
        Object.DontDestroyOnLoad(go);

        SceneSwitcher.SwitchToScene(DVScenes.Game);

        TrainStress.globalIgnoreStressCalculation = true;

    }

    private void OnClientboundRemoveLoadingScreen(ClientboundRemoveLoadingScreenPacket packet)
    {
        Log("World sync finished, removing loading screen");
        LoadingScreen.Finish();

        // Starting items this game made itself are only this game's until the server adopts them.
        ClientItemManager.Instance?.AdoptUnboundInventoryItemsSoon();

        chatGUI = ChatGUI.Create(isSinglePlayer);
    }

    private void OnCommonChatPacket(CommonChatPacket packet)
    {
        chatGUI?.ReceiveMessage(packet.message);
    }



    private void SendPacketToServer<T>(T packet, DeliveryMethod deliveryMethod) where T : class, new()
    {
        if (ShouldBlockObserverMutation(packet))
            return;
        SendPacket(serverPeer, packet, deliveryMethod);
    }

    private void SendNetSerializablePacketToServer<T>(T packet, DeliveryMethod deliveryMethod) where T : INetSerializable, new()
    {
        if (ShouldBlockObserverMutation(packet))
            return;
        SendNetSerializablePacket(serverPeer, packet, deliveryMethod);
    }

    private bool ShouldBlockObserverMutation<T>(T packet)
    {
        if (Multiplayer.Settings?.ObserverMode != true || ReferenceEquals(packet, null))
            return false;
        Type packetType = packet.GetType();
        if (!ObserverMutationPacketTypes.Contains(packetType))
            return false;
        if (observerBlockedPacketWarnings.Add(packetType))
            Log($"Observer mode blocked outbound mutation {packetType.Name}");
        DebugRuntime.Publish("observer", "observer.mutation-blocked", DebugRuntimeSide.Client,
            entityType: "Packet", entityId: packetType.Name,
            data: new() { ["packetType"] = packetType.FullName ?? packetType.Name });
        return true;
    }


    public void SendExternalPacketToServer<T>(T packet, bool reliable) where T : class, IPacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        SendPacketToServer(packet, deliveryMethod);
    }

    public void SendExternalSerializablePacketToServer<T>(T packet, bool reliable) where T : class, ISerializablePacket, new()
    {
        var deliveryMethod = reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable;
        var wrapper = new ExternalSerializablePacketWrapper<T> { Packet = packet };
        SendNetSerializablePacketToServer(wrapper, deliveryMethod);
    }

    private void SendLoadStateUpdate(PlayerLoadingState newState, TrackGraphFingerprint trackGraph = default,
        ShopCatalogueFingerprint shops = default)
    {
        Log($"Sending Load State {newState}");
        SendPacketToServer(new ServerboundLoadStateUpdatePacket
        {
            LoadState = newState,
            TrackGraphHash = trackGraph.Hash,
            TrackCount = (ushort)trackGraph.Tracks,
            JunctionCount = (ushort)trackGraph.Junctions,
            TurntableCount = (ushort)trackGraph.Turntables,
            ShopCatalogueHash = shops.Hash,
            ShopCount = (ushort)shops.Shops,
            ShopItemCount = (ushort)shops.Items,
        }, DeliveryMethod.ReliableOrdered);
        LoadingState = newState;
    }

    public void SendPlayerPosition(ServerboundPlayerPositionPacket packet, bool reliable)
    {
        SendPacketToServer(packet, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
    }

    public void SendPlayerPreferences(Dictionary<PlayerPreference, string> preferences)
    {
        if (preferences == null || preferences.Count == 0)
        {
            LogWarning("SendPlayerPreferences() called with null or empty preferences");
            return;
        }
        SendPacketToServer(new ServerboundPlayerPreferenceUpdatePacket
        {
            PreferenceKeys = Array.ConvertAll(preferences.Keys.ToArray(), item => (byte)item),
            PreferenceValues = preferences.Values.ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    // Topology commands sent inside this scope come from a remote controller, used from a distance.
    private int remoteTopologyDepth;

    private sealed class ScopeExit : IDisposable
    {
        private Action onExit;
        public ScopeExit(Action onExit) => this.onExit = onExit;
        public void Dispose() { onExit?.Invoke(); onExit = null; }
    }

    /// <summary>A topology snapshot is waiting to be applied (and being retried).</summary>
    internal bool TrainTopologyPending => trainTopology.HasPending;

    public void SendChat(string message)
    {
        SendPacketToServer(new CommonChatPacket
        {
            message = message
        }, DeliveryMethod.ReliableUnordered);

    }

    private readonly Dictionary<ushort, uint> trainsetRelocationRevisions = new();
    private readonly Dictionary<ushort, uint> pendingTrainsetRelocationRevisions = new();
    }
