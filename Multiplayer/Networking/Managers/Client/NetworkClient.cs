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
using Multiplayer.API;
using Multiplayer.Components.MainMenu;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.UI;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.SaveGame;
using Multiplayer.Core.Trains;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Data.Player;
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
using Multiplayer.Networking.Serialization;
using Multiplayer.Networking.TransportLayers;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Patches.MainMenu;
using Multiplayer.Patches.SaveGame;
using Multiplayer.Patches.World;
using Multiplayer.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Networking.Managers.Client;

public class NetworkClient : NetworkManager
{
    public event Action<ClientboundLostItemRetrieveResultPacket> LostItemRetrieveCompleted;
    public event Action<ClientboundContainerViewPacket> ContainerViewReceived;
    public event Action<ClientboundContainerMutationResultPacket> ContainerMutationCompleted;
    protected override string LogPrefix => "[Client]";

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
        typeof(ServerboundShopPaymentPacket),
        typeof(ServerboundHazmatIgnitePacket),
        typeof(ServerboundShopPurchasePacket),
        typeof(ServerboundItemBelowMapPacket),
        typeof(ServerboundContainerMutationPacket),
        typeof(ServerboundLostItemRetrievePacket),
        typeof(ServerboundItemSpatialSamplePacket),
        typeof(ServerboundItemSpatialSettlementPacket),
        typeof(ServerboundItemTrainWakeWitnessPacket),
        typeof(ServerboundJobBookletSummonRequestPacket),
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

    public readonly ClientPlayerManager ClientPlayerManager;
    public readonly Dictionary<byte, ClientPlayerWrapper> PlayerWrapperCache = [];
    public IReadOnlyCollection<ClientPlayerWrapper> ClientPlayerWrappers => PlayerWrapperCache.Values;

    internal PlayerLoadingState LoadingState { get; set; } = PlayerLoadingState.None;
    internal uint trainSetsToSpawn = uint.MaxValue;
    internal uint trainSetsSpawned = 0;
    private readonly TrainBaselineReceipt trainBaseline = new();
    private readonly TrainTopologyReconciler trainTopology = new();
    private bool trainTopologyRetryRunning;
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

    private ChatGUI chatGUI;
    private readonly bool isSinglePlayer;

    private bool isAlsoHost;
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
        ClientPlayerManager = new ClientPlayerManager();

        // A static method, so re-subscribing on every new client replaces instead of piling up.
        WorldStreamingInit.LoadingFinished -= NetworkedPlayer.CaptureItemAnchorOffset;
        WorldStreamingInit.LoadingFinished += NetworkedPlayer.CaptureItemAnchorOffset;

        // A dedicated host's own client is a service identity, not the install's player.
        Username = NetworkLifecycle.Instance.IsDedicatedHost ? DedicatedHost.ServiceName : Multiplayer.Settings.GetUserName();
        characterModelId = Multiplayer.Settings.CharacterId;

        Settings.OnSettingsUpdated += OnSettingsUpdated;
    }

    public void Start(string address, int port, string password, bool isSinglePlayer, Action<DisconnectReason, string> onDisconnect)
    {
        LogDebug(() => $"NetworkClient Constructor");

        this.onDisconnect = onDisconnect;
        //netManager.Start();
        base.Start();

        // The host's own client is trusted by its token and needs no ticket.
        bool hosting = NetworkLifecycle.Instance.IsServerRunning;
        ulong steamId = hosting ? 0 : PlayerIdentity.LocalSteamId;
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
            Guid = (NetworkLifecycle.Instance.IsDedicatedHost ? Multiplayer.Settings.GetServiceGuid() : Multiplayer.Settings.GetGuid()).ToByteArray(),
            SteamId = ticket.Length > 0 ? steamId : 0,
            AuthTicket = ticket,
            HostToken = hosting ? Managers.Server.PlayerAuthentication.HostToken : [],
            Password = password,
            BuildVersion = MainMenuControllerPatch.MenuProvider.BuildVersionString,
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

        isAlsoHost = NetworkLifecycle.Instance.IsServerRunning;
        originalSession = UserManager.Instance.CurrentUser.CurrentSession;

        LogDebug(() => $"NetworkClient.Start() isAlsoHost: {isAlsoHost}, Original session is Null: {originalSession == null}");
    }

    public override void Stop()
    {
        Log("Stopping client");
        trainsetRelocationRevisions.Clear();
        pendingTrainsetRelocationRevisions.Clear();
        if (!isAlsoHost && originalSession != null)
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
        netPacketProcessor.SubscribeReusable<ClientboundServerLoadingPacket>(OnClientboundServerLoadingPacket);
        netPacketProcessor.SubscribeReusable<ClientboundLoginResponsePacket>(OnClientboundLoginResponsePacket);
        netPacketProcessor.SubscribeReusable<ClientboundDisconnectPacket>(OnClientboundDisconnectPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPingUpdatePacket>(OnClientboundPingUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundLoadStateInfoPacket>(OnClientboundLoadStateInfoPacket);
        netPacketProcessor.SubscribeReusable<ClientboundRemoveLoadingScreenPacket>(OnClientboundRemoveLoadingScreen);

        netPacketProcessor.SubscribeReusable<ClientboundGameParamsPacket>(OnClientboundGameParamsPacket);
        netPacketProcessor.SubscribeReusable<ClientboundSaveGameDataPacket>(OnClientboundSaveGameDataPacket);
        netPacketProcessor.SubscribeReusable<ClientboundRailwayStatePacket>(OnClientboundRailwayStatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundSleepStatePacket>(OnClientboundSleepStatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundHazmatTilesPacket>(OnClientboundHazmatTilesPacket);
        netPacketProcessor.SubscribeReusable<ClientboundShopStockPacket>(OnClientboundShopStockPacket);
        netPacketProcessor.SubscribeReusable<ClientboundCustomizationsPacket>(OnClientboundCustomizationsPacket);


        // General Sync
        netPacketProcessor.SubscribeNetSerializable<ClientboundRpcResponsePacket>(OnClientboundRpcResponsePacket);
        netPacketProcessor.SubscribeReusable<ClientboundTickSyncPacket>(OnClientboundTickSyncPacket);
        netPacketProcessor.SubscribeReusable<ClientboundWeatherPacket>(OnClientboundWeatherPacket);
        netPacketProcessor.SubscribeReusable<ClientboundTimeAdvancePacket>(OnClientboundTimeAdvancePacket);
        netPacketProcessor.SubscribeReusable<CommonChangeJunctionPacket>(OnCommonChangeJunctionPacket);
        netPacketProcessor.SubscribeReusable<CommonRotateTurntablePacket>(OnCommonRotateTurntablePacket);


        // Player Management
        netPacketProcessor.SubscribeReusable<ClientboundPlayerJoinedPacket>(OnClientboundPlayerJoinedPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPlayerDisconnectPacket>(OnClientboundPlayerDisconnectPacket);

        netPacketProcessor.SubscribeReusable<ClientboundPlayerPositionPacket>(OnClientboundPlayerPositionPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPlayerPreferencesUpdatePacket>(OnClientboundPlayerPreferencesUpdatePacket);


        // Train Sync
        netPacketProcessor.SubscribeReusable<ClientboundSpawnTrainSetPacket>(OnClientboundSpawnTrainSetPacket);
        netPacketProcessor.SubscribeReusable<ClientboundTrainBaselineBeginPacket>(OnClientboundTrainBaselineBeginPacket);
        netPacketProcessor.SubscribeReusable<ClientboundTrainBaselineCompletePacket>(OnClientboundTrainBaselineCompletePacket);
        netPacketProcessor.SubscribeReusable<ClientboundTrainTopologySnapshotPacket>(OnClientboundTrainTopologySnapshotPacket);
        netPacketProcessor.SubscribeReusable<ClientboundDestroyTrainCarPacket>(OnClientboundDestroyTrainCarPacket);
        netPacketProcessor.SubscribeReusable<ClientboundRerailTrainPacket>(OnClientboundRerailTrainPacket);
        netPacketProcessor.SubscribeReusable<ClientboundMoveTrainPacket>(OnClientboundMoveTrainPacket);
        netPacketProcessor.SubscribeReusable<ClientboundTrainsetRelocationPacket>(OnClientboundTrainsetRelocationPacket);

        netPacketProcessor.SubscribeReusable<ClientboundTrainsetPhysicsPacket>(OnClientboundTrainPhysicsPacket);
        netPacketProcessor.SubscribeReusable<ClientboundBrakeStateUpdatePacket>(OnClientboundBrakeStateUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundTrainControlCommitPacket>(OnClientboundTrainControlCommitPacket);

        netPacketProcessor.SubscribeReusable<CommonCouplerInteractionPacket>(OnCommonCouplerInteractionPacket);


        netPacketProcessor.SubscribeReusable<CommonPaintThemePacket>(OnCommonPaintThemePacket);
        netPacketProcessor.SubscribeReusable<ClientboundRestorationStateChangePacket>(OnClientboundRestorationStateChangePacket);

        netPacketProcessor.SubscribeReusable<ClientboundTrainControlAuthorityUpdatePacket>(OnClientboundTrainControlAuthorityUpdatePacket);

        netPacketProcessor.SubscribeReusable<ClientboundCargoStatePacket>(OnClientboundCargoStatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundExplosionPacket>(OnClientboundExplosionPacket);
        netPacketProcessor.SubscribeReusable<ClientboundCargoFirePacket>(OnClientboundCargoFirePacket);
        netPacketProcessor.SubscribeReusable<ClientboundCargoHealthUpdatePacket>(OnClientboundCargoHealthUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundCarHealthUpdatePacket>(OnClientboundCarHealthUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundWarehouseControllerUpdatePacket>(OnClientboundWarehouseControllerUpdatePacket);

        netPacketProcessor.SubscribeReusable<ClientboundWindowsBrokenPacket>(OnClientboundWindowsBrokenPacket);
        netPacketProcessor.SubscribeReusable<ClientboundWindowsRepairedPacket>(OnClientboundWindowsRepairedPacket);
        netPacketProcessor.SubscribeReusable<ClientboundMoneyPacket>(OnClientboundMoneyPacket);
        netPacketProcessor.SubscribeReusable<ClientboundLicenseAcquiredPacket>(OnClientboundLicenseAcquiredPacket);
        netPacketProcessor.SubscribeReusable<ClientboundGarageUnlockPacket>(OnClientboundGarageUnlockPacket);

        // Job Sync
        netPacketProcessor.SubscribeReusable<ClientboundDebtStatusPacket>(OnClientboundDebtStatusPacket);
        netPacketProcessor.SubscribeReusable<ClientboundJobsUpdatePacket>(OnClientboundJobsUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundJobsCreatePacket>(OnClientboundJobsCreatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundJobValidateResponsePacket>(OnClientboundJobValidateResponsePacket);
        netPacketProcessor.SubscribeReusable<ClientboundJobReportArtifactPacket>(OnClientboundJobReportArtifactPacket);
        netPacketProcessor.SubscribeReusable<ClientboundTaskUpdatePacket>(OnClientboundTaskUpdatePacket);

        // World Sync
        netPacketProcessor.SubscribeReusable<CommonItemsBulkUpdatePacket>(OnCommonItemsBulkUpdatePacket);
        netPacketProcessor.SubscribeReusable<CommonItemUpdatePacket>(OnCommonItemUpdatePacket);
        netPacketProcessor.SubscribeReusable<ClientboundItemAdoptionPacket>(OnClientboundItemAdoptionPacket);
        netPacketProcessor.SubscribeReusable<ClientboundItemRecallPreparePacket>(OnClientboundItemRecallPreparePacket);
        netPacketProcessor.SubscribeReusable<ClientboundItemRecallResultPacket>(OnClientboundItemRecallResultPacket);
        netPacketProcessor.SubscribeReusable<ClientboundLostItemsSnapshotPacket>(OnClientboundLostItemsSnapshotPacket);
        netPacketProcessor.SubscribeReusable<ClientboundLostItemRetrieveResultPacket>(OnClientboundLostItemRetrieveResultPacket);
        netPacketProcessor.SubscribeReusable<ClientboundContainerViewPacket>(OnClientboundContainerViewPacket);
        netPacketProcessor.SubscribeReusable<ClientboundContainerMutationResultPacket>(OnClientboundContainerMutationResultPacket);
        netPacketProcessor.SubscribeReusable<ClientboundWorldItemCataloguePacket>(OnClientboundWorldItemCataloguePacket);
        netPacketProcessor.SubscribeReusable<ClientboundItemSpatialLeasePacket>(OnClientboundItemSpatialLeasePacket);
        netPacketProcessor.SubscribeReusable<ClientboundItemSpatialSamplePacket>(OnClientboundItemSpatialSamplePacket);
        netPacketProcessor.SubscribeReusable<ClientboundItemSpatialCommitPacket>(OnClientboundItemSpatialCommitPacket);
        netPacketProcessor.SubscribeReusable<CommonPitStopInteractionPacket>(OnCommonPitStopInteractionPacket);
        netPacketProcessor.SubscribeNetSerializable<CommonPitStopPlugInteractionPacket>(OnCommonPitStopPlugInteractionPacket);
        netPacketProcessor.SubscribeReusable<ClientboundPitStopBulkUpdatePacket>(OnClientboundPitStopBulkUpdatePacket);
        netPacketProcessor.SubscribeReusable<CommonCashRegisterWithModulesActionPacket>(OnCommonCashRegisterWithModulesActionPacket);
        netPacketProcessor.SubscribeReusable<CommonGenericSwitchStatePacket>(OnCommonGenericSwitchStatePacket);

        netPacketProcessor.SubscribeReusable<CommonChatPacket>(OnCommonChatPacket);
    }

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
    internal void RegisterReadyBlock(string modName)
    {
        Log($"Ready Block has been registered by {modName}");

        if (readyBlocks.Contains(modName))
            return;

        readyBlocks.Add(modName);
    }

    internal void CancelReadyBlock(string modName)
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
        NetworkedItemManager.Instance.QuarantineUnboundClientItems();
        SendWorldItemCatalogue();
        Log($"Initialising Cash Registers...");
        NetworkedCashRegisterWithModules.InitialiseCashRegisters();
        Log($"Initialising Pit Stops...");
        NetworkedPitStopStation.InitialisePitStops();

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
        SendLoadStateUpdate(PlayerLoadingState.ReadyForWorldState);
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
         */

        //TODO: implement
        yield return new WaitForSeconds(0.25f);

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

    public ClientPlayerWrapper GetWrapper(NetworkedPlayer networkedPlayer)
    {
        if (!PlayerWrapperCache.TryGetValue(networkedPlayer.PlayerId, out var wrapper))
        {
            wrapper = new ClientPlayerWrapper(networkedPlayer);
            PlayerWrapperCache[networkedPlayer.PlayerId] = wrapper;
        }
        return wrapper;
    }

    #region Net Events

    public override void OnPeerConnected(ITransportPeer peer)
    {
        serverPeer = peer;
    }

    public override void OnPeerDisconnected(ITransportPeer peer, DisconnectReason disconnectReason)
    {

        LogDebug(() => $"OnPeerDisconnected({peer.Id}, {disconnectReason}) disconnect message: {disconnectMessage}");

        NetworkLifecycle.Instance.Stop();

        TrainStress.globalIgnoreStressCalculation = false;

        if (MainMenuThingsAndStuff.Instance != null)
        {
            //MainMenuThingsAndStuff.Instance.SwitchToDefaultMenu();
            LogDebug(() => $"OnPeerDisconnected() queuing GoBackToMainMenu via NetworkLifecycle");
            NetworkLifecycle.Instance.TriggerMainMenuEventLater();
        }
        else
        {
            LogDebug(() => $"OnPeerDisconnected() {nameof(MainMenuThingsAndStuff.Instance)} is null, calling GoBackToMainMenu directly");
            MainMenu.GoBackToMainMenu();
            NetworkLifecycle.Instance.TriggerMainMenuEventLater();
        }

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

    #endregion

    #region Listeners

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

            DebugRuntime.SetRole(NetworkLifecycle.Instance.IsHost() ? "host" : "client", Username, PlayerId);

            if (NetworkLifecycle.Instance.IsHost())
            {
                // Host can skip straight to world state sync as they already have the world loaded
                SendLoadStateUpdate(PlayerLoadingState.ReadyForWorldState);
                return;
            }

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
        ClientPlayerManager.AddPlayer(packet.PlayerId, packet.Username, packet.CrewName, packet.CharacterId, packet.IsVR);

        ClientPlayerManager.UpdatePosition(packet.PlayerId, packet.TrackingData, packet.Posture, packet.IsOnCar, packet.CarID);
    }

    //For other player left the game
    private void OnClientboundPlayerDisconnectPacket(ClientboundPlayerDisconnectPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Player", packet.PlayerId.ToString());
        Log($"Received player disconnect packet for player id: {packet.PlayerId}");
        ClientPlayerManager.RemovePlayer(packet.PlayerId);
    }

    //For server shutting down / player kicked
    private void OnClientboundDisconnectPacket(ClientboundDisconnectPacket packet)
    {
        if (packet.Kicked)
        {
            Log($"Player was kicked!");
            disconnectMessage = "You were kicked!";
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
        ClientPlayerManager.UpdatePosition(packet.PlayerId, packet.TrackingData, packet.Posture, packet.IsOnCar, packet.CarID);
    }

    private void OnClientboundPlayerPreferencesUpdatePacket(ClientboundPlayerPreferencesUpdatePacket packet)
    {
        Log($"Received player preferences update for '{packet.PlayerId}'");

        if (packet.PlayerId == PlayerId)
        {
            if (packet.GetPreferencesDictionary().TryGetValue(PlayerPreference.CrewName, out string crewName))
                CrewName = crewName;
        }

        ClientPlayerManager.UpdatePreferences(packet.PlayerId, packet.GetPreferencesDictionary());
    }

    private void OnClientboundPingUpdatePacket(ClientboundPingUpdatePacket packet)
    {
        ClientPlayerManager.UpdatePing(packet.PlayerId, packet.Ping);
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

    private void OnClientboundGameParamsPacket(ClientboundGameParamsPacket packet)
    {
        LogDebug(() => $"Received {nameof(ClientboundGameParamsPacket)} ({packet.SerializedGameParams.Length} chars)");
        if (Globals.G.GameParams != null)
            packet.Apply(Globals.G.GameParams);
        if (Globals.G.gameParamsInstance != null)
            packet.Apply(Globals.G.gameParamsInstance);

        TimeAdvancePatch.FastTravelAdvancesTime = packet.FastTravelAdvancesTime;
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

    private void OnClientboundWeatherPacket(ClientboundWeatherPacket packet)
    {
        if (LoadingState < PlayerLoadingState.Complete)
            Log("Received weather state");

        // The host's clock arrives one trip late, so applying it as-is stepped the client's clock
        // back a little every update. Time-driven consumers read a backward step as a whole day
        // passing (a lit flashlight's battery emptied at once). Keep a clock that is only slightly
        // ahead; real changes (the host setting the time) are far larger.
        if (LoadingState >= PlayerLoadingState.Complete && WeatherDriver.Instance.manager != null)
        {
            double local = WeatherDriver.Instance.manager.RealDateTime.ToOADate();
            double behind = (local - packet.OADate) * TimeSpan.FromDays(1).TotalSeconds;
            if (behind > 0 && behind < MaxWeatherClockLeadSeconds)
                packet.OADate = local;
        }

        WeatherDriver.Instance.LoadSaveData(JObject.FromObject(packet), Globals.G.GameParams.WeatherEditorAlwaysAllowed);
    }

    private void OnClientboundRemoveLoadingScreen(ClientboundRemoveLoadingScreenPacket packet)
    {
        Log("World sync finished, removing loading screen");

        DisplayLoadingInfo displayLoadingInfo = Object.FindObjectOfType<DisplayLoadingInfo>();
        if (displayLoadingInfo != null)
            displayLoadingInfo.OnLoadingFinished();
        else
            LogWarning($"Received {nameof(ClientboundRemoveLoadingScreenPacket)} but couldn't find {nameof(DisplayLoadingInfo)}");

        // Only FinishLoading() leaves loading mode. Without it a VR player is left in the world (the
        // VR loading overlay closes by itself at 100%) with the hand controllers switched off, their
        // own controllers showing and no teleport (#72).
        if (LoadingScreenManager.IsLoading)
        {
            LogWarning("Still in loading mode after removing the loading screen, finishing it");
            LoadingScreenManager.Instance.FinishLoading();
        }

        // Starting items this game made itself are only this game's until the server adopts them.
        if (!isAlsoHost)
            NetworkedItemManager.Instance?.AdoptUnboundInventoryItemsSoon();

        //if not single player, add in chat (nobody reads it on a dedicated host, and building it
        //pauses the game)
        if (!isSinglePlayer && !NetworkLifecycle.Instance.IsDedicatedHost)
        {
            GameObject common = GameObject.Find("[MAIN]/[GameUI]/[NewCanvasController]/Auxiliary Canvas, EventSystem, Input Module");
            if (common != null)
            {
                //
                GameObject chat = new("Chat GUI", typeof(ChatGUI));
                chat.transform.SetParent(common.transform, false);
                chatGUI = chat.GetComponent<ChatGUI>();
            }
        }
    }

    private void OnClientboundTimeAdvancePacket(ClientboundTimeAdvancePacket packet)
    {
        TimeAdvance.AdvanceTime(packet.amountOfTimeToSkipInSeconds);
    }

    private void OnClientboundShopStockPacket(ClientboundShopStockPacket packet)
    {
        GlobalShopController shops = GlobalShopController.Instance;
        if (shops == null || packet.ItemPrefabNames == null || packet.PurchasedItems == null)
            return;
        for (int i = 0; i < packet.ItemPrefabNames.Length && i < packet.PurchasedItems.Length; i++)
        {
            ShopItemData data = shops.GetShopItemData(packet.ItemPrefabNames[i]);
            if (data != null)
                data.purchasedItems = packet.PurchasedItems[i];
        }
        shops.Fire_GlobalShopDataChanged();
    }

    private void OnClientboundRailwayStatePacket(ClientboundRailwayStatePacket packet)
    {
        Log("Received railway state");

        // A junction or turntable missing on this side used to return here without marking the
        // railway loaded, and the join then waited for it forever. Apply what exists and carry on.
        int missingJunctions = 0, missingTurntables = 0;
        for (int i = 0; i < packet.SelectedJunctionBranches.Length; i++)
        {
            if (!NetworkedJunction.Get((ushort)(i + 1), out NetworkedJunction junction))
            {
                missingJunctions++;
                continue;
            }
            junction.Switch((byte)Junction.SwitchMode.NO_SOUND, packet.SelectedJunctionBranches[i], true);
        }

        for (int i = 0; i < packet.TurntableRotations.Length; i++)
        {
            if (!NetworkedTurntable.Get((byte)(i + 1), out NetworkedTurntable turntable))
            {
                missingTurntables++;
                continue;
            }
            turntable.SetRotation(packet.TurntableRotations[i], true, true);
        }

        if (missingJunctions > 0 || missingTurntables > 0)
            LogWarning($"Railway state: {missingJunctions} of {packet.SelectedJunctionBranches.Length} junctions and " +
                       $"{missingTurntables} of {packet.TurntableRotations.Length} turntables not found locally; " +
                       "those keep their local position (mismatched mods or game versions?)");

        railwayStateLoaded = true;
    }

    private void OnClientboundCustomizationsPacket(ClientboundCustomizationsPacket packet)
    {
        Log("Received customizations");

        WorldCustomization.I.ClearHoles();

        foreach (var hole in packet.Holes)
            WorldCustomization.I.AddHole(hole.Position, hole.Rotation);
    }

    private void OnCommonChangeJunctionPacket(CommonChangeJunctionPacket packet)
    {
        if (!NetworkedJunction.Get(packet.NetId, out NetworkedJunction junction))
            return;
        junction.Switch(packet.Mode, packet.SelectedBranch);
    }

    private void OnCommonRotateTurntablePacket(CommonRotateTurntablePacket packet)
    {
        if (!NetworkedTurntable.Get(packet.NetId, out NetworkedTurntable turntable))
            return;
        turntable.SetRotation(packet.rotation);
    }

    private void OnClientboundSpawnTrainSetPacket(ClientboundSpawnTrainSetPacket packet)
    {
        if (packet == null || packet.SpawnParts == null)
        {
            RejectTrainBaseline("spawn packet contained a null trainset");
            return;
        }

        if (packet.BaselineId != 0)
        {
            TrainBaselineDisposition disposition =
                trainBaseline.Admit(packet.BaselineId, packet.Sequence, out string admissionFailure);
            if (disposition == TrainBaselineDisposition.Duplicate)
                return;
            if (disposition == TrainBaselineDisposition.Reject)
            {
                RejectTrainBaseline(admissionFailure);
                return;
            }
        }

        LogDebug(() => $"Spawning trainset consisting of {string.Join(", ", packet.SpawnParts.Select(p => p == null ? "<null>" : $"{p.CarId} ({p.LiveryId}) with netId: {p.NetId}"))}");

        if (!NetworkedCarSpawner.TrySpawnCars(packet.SpawnParts, packet.AutoCouple, false, out string failure))
        {
            RejectTrainBaseline($"trainset {packet.Sequence} could not be applied: {failure}");
            return;
        }

        if (packet.BaselineId != 0)
        {
            if (!trainBaseline.Commit(packet.Sequence, (uint)packet.SpawnParts.Length))
            {
                RejectTrainBaseline(trainBaseline.Failure);
                return;
            }
            trainSetsSpawned++;
        }
    }

    private void OnClientboundTrainBaselineBeginPacket(ClientboundTrainBaselineBeginPacket packet)
    {
        if (packet == null)
        {
            RejectTrainBaseline("server sent an invalid manifest identity");
            return;
        }

        trainSetsToSpawn = packet.TrainsetCount;
        trainSetsSpawned = 0;
        trainTopology.Reset(packet.BaselineId);
        if (!trainBaseline.Begin(packet.BaselineId, packet.TrainsetCount, packet.CarCount,
            packet.Failure))
        {
            RejectTrainBaseline(trainBaseline.Failure);
            return;
        }
        Log($"Received rolling-stock baseline {packet.BaselineId}: {packet.TrainsetCount} trainsets, {packet.CarCount} cars");
    }

    private void OnClientboundTrainBaselineCompletePacket(ClientboundTrainBaselineCompletePacket packet)
    {
        if (packet == null)
        {
            RejectTrainBaseline("server sent a null completion manifest");
            return;
        }
        if (!trainBaseline.TryComplete(packet.BaselineId, packet.TrainsetCount, packet.CarCount))
        {
            RejectTrainBaseline(trainBaseline.Failure);
            return;
        }
        if (!trainTopology.BaselineApplied || trainTopology.BaselineId != packet.BaselineId)
        {
            if (trainTopology.HasPending)
            {
                StartTrainTopologyRetry();
                LogWarning($"Waiting for rolling-stock baseline {packet.BaselineId} native readiness");
                return;
            }
            RejectTrainBaseline("canonical train topology baseline was not received or was invalid");
            return;
        }
    }

    private void OnClientboundTrainTopologySnapshotPacket(
        ClientboundTrainTopologySnapshotPacket packet)
    {
        if (packet?.OperationId != 0 && packet.Result != TrainTopologyResultCode.Accepted)
            LogWarning($"Train topology operation {packet.OperationId} was rejected: " +
                       $"{packet.Result}; authoritative revision {packet.TopologyRevision}");

        if (trainTopology.Receive(packet, out string failure))
        {
            DebugRuntime.Publish("train", "train.topology-snapshot-applied",
                DebugRuntimeSide.Client, entityType: "Topology",
                entityId: packet.TopologyRevision.ToString(),
                data: DebugValueSnapshotter.SnapshotObject(new
                {
                    packet.BaselineId,
                    packet.TopologyRevision,
                    packet.OperationId,
                    result = packet.Result.ToString(),
                    endpointCount = packet.Endpoints?.Length ?? 0,
                    brakeLineCount = packet.BrakeLines?.Length ?? 0
                }));
            return;
        }

        LogWarning($"Deferred/rejected topology snapshot r{packet?.TopologyRevision}: {failure}");
        if (trainTopology.HasPending)
        {
            StartTrainTopologyRetry();
            return;
        }
        if (packet?.BaselineId != 0 && LoadingState == PlayerLoadingState.ReadyForTrainSets)
        {
            RejectTrainBaseline(failure);
            return;
        }
        if (LoadingState > PlayerLoadingState.ReadyForTrainSets &&
            !failure.StartsWith("endpoint-not-ready:"))
            RequestTrainTopologySync();
    }

    private void StartTrainTopologyRetry()
    {
        if (trainTopologyRetryRunning)
            return;
        trainTopologyRetryRunning = true;
        SingletonBehaviour<CoroutineManager>.Instance.Run(RetryPendingTrainTopology());
    }

    private IEnumerator RetryPendingTrainTopology()
    {
        float deadline = Time.realtimeSinceStartup + 15f;
        string failure = string.Empty;
        try
        {
            while (trainTopology.HasPending && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.05f);
                if (trainTopology.TryApplyPending(out failure))
                {
                    Log($"Applied deferred canonical train topology r{trainTopology.Revision}");
                    yield break;
                }
            }

            if (!trainTopology.HasPending)
                yield break;
            string reason = string.IsNullOrEmpty(failure)
                ? "native readiness timeout"
                : failure;
            if (LoadingState == PlayerLoadingState.ReadyForTrainSets &&
                trainBaseline.Started && !trainTopology.BaselineApplied)
                RejectTrainBaseline("canonical train topology timeout: " + reason);
            else
            {
                LogWarning("Canonical train topology retry timed out: " + reason);
                RequestTrainTopologySync();
            }
        }
        finally
        {
            trainTopologyRetryRunning = false;
        }
    }

    private void RejectTrainBaseline(string reason)
    {
        if (LoadingState == PlayerLoadingState.ReadyForTrainSets)
            trainBaseline.Reject(reason);
        LogError($"Rolling-stock baseline rejected: {reason}");
    }

    private void OnClientboundDestroyTrainCarPacket(ClientboundDestroyTrainCarPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            LogWarning($"Received DestroyTrainCarPacket for netId: {packet.NetId}, but NetworkedTrainCar was not found.");
            return;
        }

        Log($"Received DestroyTrainCarPacket for [{netTrainCar.CurrentID} {packet.NetId}]");

        // Players on the car (us included) are stepped off it by CarDeletePlayerSafetyPatch;
        // clearing PlayerManager.Car alone left our player parented to the deleted car.

        //Protect other players from getting deleted in race conditions - this should be a temporary fix, if another playe's game object is deleted we should just recreate it
        if (netTrainCar == null || netTrainCar.gameObject == null || netTrainCar.TrainCar == null)
        {
            LogDebug(() => $"OnClientboundDestroyTrainCarPacket({packet?.NetId}) networkedTrainCar: {netTrainCar != null}, trainCar: {netTrainCar?.TrainCar != null}");
        }
        else
        {
            NetworkedPlayer[] componentsInChildren = (netTrainCar?.gameObject != null) ? netTrainCar.GetComponentsInChildren<NetworkedPlayer>() : [];

            foreach (NetworkedPlayer networkedPlayer in componentsInChildren)
            {
                networkedPlayer.UpdateCar(0);
            }

            netTrainCar.TrainCar.UpdateJobIdOnCarPlates(string.Empty);
            CarSpawner.Instance.DeleteCar(netTrainCar.TrainCar);
        }
    }

    public void OnClientboundTrainPhysicsPacket(ClientboundTrainsetPhysicsPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client);
        //LogDebug(() => $"Received Physics packet for netId: {packet.FirstNetId}, tick: {packet.Tick}");
        NetworkTrainsetWatcher.Instance.Client_HandleTrainsetPhysicsUpdate(packet);
    }

    private void OnCommonCouplerInteractionPacket(CommonCouplerInteractionPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
        {
            LogError($"OnCommonCouplerInteractionPacket netId: {packet.NetId}, TrainCar not found!");
            return;
        }

        netTrainCar.Common_ReceiveCouplerInteraction(packet);
    }

    private void OnClientboundTrainControlAuthorityUpdatePacket(ClientboundTrainControlAuthorityUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        networkedTrainCar.Client_ReceiveAuthorityUpdate(packet.PortNetId, packet.State);
    }

    private void OnClientboundBrakeStateUpdatePacket(ClientboundBrakeStateUpdatePacket packet)
    {
        if (packet.TopologyRevision > trainTopology.Revision)
        {
            if (packet.TopologyRevision != lastDeferredBrakeTopologyRevision ||
                Time.realtimeSinceStartup >= nextDeferredBrakeTopologyWarningAt)
            {
                lastDeferredBrakeTopologyRevision = packet.TopologyRevision;
                nextDeferredBrakeTopologyWarningAt = Time.realtimeSinceStartup + 2f;
                LogWarning($"Deferred brake state for topology r{packet.TopologyRevision}; local r{trainTopology.Revision}");
            }
            RequestTrainTopologySync();
            return;
        }
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        if (!networkedTrainCar.Client_ReceiveBrakeStateUpdate(packet))
        {
            LogWarning($"Brake line membership mismatch for car {packet.NetId}; requesting topology resync");
            RequestTrainTopologySync();
        }

        //LogDebug(() => $"Received Brake Pressures netId {packet.NetId}: {packet.MainReservoirPressure}, {packet.IndependentPipePressure}, {packet.BrakePipePressure}, {packet.BrakeCylinderPressure}");
    }

    private void OnClientboundExplosionPacket(ClientboundExplosionPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar car) || car == null)
            return;
        Log($"The host's {car.ID} exploded ({(global::Multiplayer.Patches.Train.ExplosionSync.Kind)packet.Kind})");
        global::Multiplayer.Patches.Train.ExplosionSync.ClientPlay(car, (global::Multiplayer.Patches.Train.ExplosionSync.Kind)packet.Kind, packet.Index);
    }

    private void OnClientboundCargoFirePacket(ClientboundCargoFirePacket packet)
    {
        if (NetworkedTrainCar.TryGet(packet.CarNetId, out TrainCar car) && car != null)
            global::Multiplayer.Patches.Train.CargoFireSync.Client_Receive(car, packet.Index, (global::Multiplayer.Patches.Train.CargoFireSync.FireState)packet.State);
    }

    private void OnClientboundCargoStatePacket(ClientboundCargoStatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        LogDebug(() => $"OnClientboundCargoStatePacket() {networkedTrainCar.CurrentID}, IsLoading: {packet.IsLoading}, CargoType: {packet.CargoTypeNetId}, CargoAmount: {packet.CargoAmount}, Health: {packet.CargoHealth}, CargoModelIndex: {packet.CargoModelIndex}, WarehouseMachineId: {packet.WarehouseMachineNetId}");

        networkedTrainCar.CargoModelIndex = packet.CargoModelIndex;
        Car logicCar = networkedTrainCar.TrainCar.logicCar;

        if (logicCar == null)
        {
            LogWarning($"OnClientboundCargoStatePacket() Failed to find logic car for [{networkedTrainCar.TrainCar.ID}, {packet.NetId}] is initialised: {networkedTrainCar.Client_Initialized}");
            return;
        }

        CargoTypeLookup.Instance.TryGet(packet.CargoTypeNetId, out CargoType cargoType);

        if (cargoType == CargoType.None && logicCar.CurrentCargoTypeInCar == CargoType.None)
            return;

        //packet.CargoAmount is the total amount, not the amount to load/unload
        float cargoAmount = Mathf.Clamp(packet.CargoAmount, 0, logicCar.capacity);

        WarehouseMachine warehouseMachine = null;
        if (packet.WarehouseMachineNetId != 0 && (!WarehouseMachineLookup.TryGet(packet.WarehouseMachineNetId, out warehouseMachine) || warehouseMachine == null))
        {
            LogWarning($"OnClientboundCargoStatePacket() Failed to find WarehouseMachine for netId {packet.WarehouseMachineNetId}");
            return;
        }

        if (packet.IsLoading)
        {
            LogDebug(() => $"OnClientboundCargoStatePacket() Loading cargo: {cargoType} into {networkedTrainCar.CurrentID}, current amount: {packet.CargoAmount}");
            //Check correct cargo is loaded and the amount is correct
            if (logicCar.LoadedCargoAmount == cargoAmount && logicCar.CurrentCargoTypeInCar == cargoType)
                return;

            //We need either no cargo or the same cargo - if it's different, we need to remove it first
            if (logicCar.CurrentCargoTypeInCar != CargoType.None && logicCar.CurrentCargoTypeInCar != cargoType)
                logicCar.DumpCargo();

            //We have the correct cargo, but not the right amount, calculate the delta
            if (logicCar.CurrentCargoTypeInCar == cargoType)
                cargoAmount -= logicCar.LoadedCargoAmount;

            if (cargoAmount > 0)
            {
                logicCar.LoadCargo(cargoAmount, cargoType, warehouseMachine);
            }

            networkedTrainCar.TrainCar.CargoDamage.LoadCargoDamageState(packet.CargoHealth);
        }
        else
        {
            LogDebug(() => $"OnClientboundCargoStatePacket() Unloading cargo: {cargoType} into {networkedTrainCar.CurrentID}, current amount: {packet.CargoAmount}");

            //Check correct cargo is loaded and the amount is correct
            if (logicCar.LoadedCargoAmount == cargoAmount && logicCar.CurrentCargoTypeInCar == cargoType)
                return;

            //If there is different cargo we need to remove it, then load the appropriate amount
            if (logicCar.CurrentCargoTypeInCar == CargoType.None || logicCar.CurrentCargoTypeInCar != cargoType)
            {
                //avoid triggering the load event by backdooring it
                logicCar.LastUnloadedCargoType = logicCar.CurrentCargoTypeInCar;
                logicCar.CurrentCargoTypeInCar = cargoType;
                logicCar.LoadedCargoAmount = cargoAmount;
            }

            //We have the correct cargo, calculate the delta
            if (logicCar.CurrentCargoTypeInCar == cargoType)
                cargoAmount = logicCar.LoadedCargoAmount - cargoAmount;

            if (cargoAmount > 0)
                logicCar.UnloadCargo(cargoAmount, cargoType, warehouseMachine);
        }
    }

    private void OnClientboundCargoHealthUpdatePacket(ClientboundCargoHealthUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;

        CargoDamageModel cargoDamageModel = networkedTrainCar.TrainCar.CargoDamage;

        if (networkedTrainCar.TrainCar == null || cargoDamageModel == null)
            return;

        float deltaHealth = cargoDamageModel.currentHealth - packet.CargoHealth;

        //LogDebug(() => $"OnClientboundCargoHealthUpdatePacket() {networkedTrainCar.CurrentID}, current health: {cargoDamageModel.currentHealth}, new health: {packet.CargoHealth}, delta: {cargoDamageModel}, applySensitivity: {packet.CargoHealth > 0}");

        if (deltaHealth > 0)
            cargoDamageModel.ApplyDamageToCargo(deltaHealth, packet.CargoHealth > 0);
    }

    private void OnClientboundCarHealthUpdatePacket(ClientboundCarHealthUpdatePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;

        packet.Health.LoadTo(trainCar);
    }

    private void OnClientboundWarehouseControllerUpdatePacket(ClientboundWarehouseControllerUpdatePacket packet)
    {
        LogDebug(() => $"OnClientboundWarehouseControllerUpdatePacket() NetId: {packet.NetId}, IsLoading: {packet.IsLoading}, JobNetId: {packet.JobNetId}, CarNetId: {packet.CarNetId}, CargoType: {packet.CargoTypeNetId}, Preset: [{(WarehouseMachineController.TextPreset)packet.Preset}, {packet.Preset}]");
        if (!NetworkedWarehouseMachineController.Get(packet.NetId, out NetworkedWarehouseMachineController networkedWarehouseMachineController))
        {
            LogWarning($"OnClientboundWarehouseControllerUpdatePacket() Failed to find networked warehouse machine controller for [{packet.NetId}]");
            return;
        }

        networkedWarehouseMachineController.ClientProcessUpdate(packet);
    }

    private void OnClientboundRerailTrainPacket(ClientboundRerailTrainPacket packet)
    {
        LogDebug(() => $"OnClientboundRerailTrainPacket() NetId: {packet.NetId}, TrackId: {packet.TrackId}, Position: {packet.Position}, Forward: {packet.Forward}, currentMove: {WorldMover.currentMove}");
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;
        TrainCar trainCar = networkedTrainCar.TrainCar;

        Log($"Rerailing [{trainCar?.ID}, {packet.NetId}] to track {networkedRailTrack?.RailTrack?.LogicTrack()?.ID}");
        LogDebug(() => $"Rerailing [{trainCar?.ID}, {packet.NetId}] track: [{networkedRailTrack?.RailTrack?.LogicTrack()?.ID}, {packet.TrackId}], raw position: {packet.Position}, adjusted position: {packet.Position + WorldMover.currentMove}, forward: {packet.Forward}");
        networkedTrainCar.Client_RerailFromHost(networkedRailTrack.RailTrack, packet.Position + WorldMover.currentMove, packet.Forward);
    }

    private void OnClientboundMoveTrainPacket(ClientboundMoveTrainPacket packet)
    {
        LogDebug(() => $"OnClientboundMoveTrainPacket() received for netId: {packet.NetId}, trackId: {packet.TrackId}, position: {packet.Position}, forward: {packet.Forward}, isTeleporting: {packet.IsTeleporting}");
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (!NetworkedRailTrack.TryGet(packet.TrackId, out NetworkedRailTrack networkedRailTrack))
            return;
        TrainCar trainCar = networkedTrainCar.TrainCar;

        Log($"Moving [{trainCar?.ID}, {packet.NetId}] to track {networkedRailTrack?.RailTrack?.LogicTrack()?.ID}");
        LogDebug(() => $"Moving [{trainCar?.ID}, {packet.NetId}] track: [{networkedRailTrack?.RailTrack?.LogicTrack()?.ID}, {packet.TrackId}], raw position: {packet.Position}, adjusted position: {packet.Position + WorldMover.currentMove}, forward: {packet.Forward}, front coupled: {trainCar.frontCoupler.coupledTo != null}, rear coupled:{trainCar.rearCoupler.coupledTo != null}, derailed: {trainCar.derailed}");

        if (!packet.IsTeleporting)
            networkedTrainCar.Client_MoveToTrackFromHost(networkedRailTrack.RailTrack, packet.Position + WorldMover.currentMove, packet.Forward);
        else
            LogDebug(() => $"OnClientboundMoveTrainPacket() netId: {packet.NetId} Attempting to move train, teleport not implemented");
    }

    private void OnClientboundWindowsBrokenPacket(ClientboundWindowsBrokenPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        DamageController damageController = trainCar.GetComponent<DamageController>();
        if (damageController == null)
            return;
        WindowsBreakingController windowsController = damageController.windows;
        if (windowsController == null)
            return;
        windowsController.BreakWindowsFromCollision(packet.ForceDirection);
    }

    private void OnClientboundWindowsRepairedPacket(ClientboundWindowsRepairedPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
            return;
        DamageController damageController = trainCar.GetComponent<DamageController>();
        if (damageController == null)
            return;
        WindowsBreakingController windowsController = damageController.windows;
        if (windowsController == null)
            return;
        windowsController.RepairWindows();
    }

    private void OnClientboundMoneyPacket(ClientboundMoneyPacket packet)
    {
        LogDebug(() => $"Received new money amount ${packet.Amount}");
        Inventory.Instance.SetMoney(packet.Amount);
    }

    private void OnClientboundLicenseAcquiredPacket(ClientboundLicenseAcquiredPacket packet)
    {
        LogDebug(() => $"Received new {(packet.IsJobLicense ? "job" : "general")} license {packet.Id}");

        if (packet.IsJobLicense)
            LicenseManager.Instance.AcquireJobLicense(Globals.G.Types.jobLicenses.Find(l => l.id == packet.Id));
        else
            LicenseManager.Instance.AcquireGeneralLicense(Globals.G.Types.generalLicenses.Find(l => l.id == packet.Id));

        foreach (CareerManagerLicensesScreen screen in Object.FindObjectsOfType<CareerManagerLicensesScreen>())
            screen.PopulateTextsFromIndex(screen.IndexOfFirstDisplayedEntry); //B99
    }

    private void OnClientboundGarageUnlockPacket(ClientboundGarageUnlockPacket packet)
    {
        LogDebug(() => $"Received new garage {packet.Id}");
        LicenseManager.Instance.UnlockGarage(Globals.G.types.garages.Find(g => g.id == packet.Id));
    }

    private void OnClientboundDebtStatusPacket(ClientboundDebtStatusPacket packet)
    {
        CareerManagerDebtControllerPatch.HasDebt = packet.HasDebt;
    }

    private void OnCommonChatPacket(CommonChatPacket packet)
    {
        chatGUI?.ReceiveMessage(packet.message);
    }

    private void OnClientboundJobsCreatePacket(ClientboundJobsCreatePacket packet)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController))
        {
            LogError($"OnClientboundJobsCreatePacket() {packet.StationNetId} does not exist!");
            return;
        }

        Log($"Received {packet.Jobs.Length} jobs for station {networkedStationController.StationController.logicStation.ID}");

        networkedStationController.AddJobs(packet.Jobs);
        DrainPendingJobReportArtifacts();
    }

    private void OnClientboundJobsUpdatePacket(ClientboundJobsUpdatePacket packet)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController))
        {
            LogError($"OnClientboundJobsUpdatePacket() {packet.StationNetId} does not exist!");
            return;
        }

        Log($"Received {packet.JobUpdates.Length} job updates for station {networkedStationController.StationController.logicStation.ID}");

        networkedStationController.UpdateJobs(packet.JobUpdates);
        DrainPendingJobReportArtifacts();
    }

    private void OnClientboundJobReportArtifactPacket(ClientboundJobReportArtifactPacket packet)
    {
        try
        {
            JobReportArtifactData artifact = JobReportArtifactData.Deserialize(packet.Payload);
            pendingJobReportArtifacts[artifact.ItemNetId] = artifact;
            DebugRuntime.Publish("job", "job.report-artifact-received", DebugRuntimeSide.Client,
                entityType: "Item", entityId: artifact.ItemNetId.ToString(), data: new Dictionary<string, object>
                {
                    ["artifactToken"] = artifact.ArtifactToken,
                    ["jobNetId"] = artifact.JobNetId,
                    ["hasDebt"] = artifact.Debt != null
                });
            TryMaterializeJobReport(artifact);
        }
        catch (Exception exception)
        {
            LogError($"Failed to decode JobReport artifact: {exception}");
            DebugRuntime.Publish("job", "job.report-artifact-rejected", DebugRuntimeSide.Client,
                DebugSeverity.Error, data: new Dictionary<string, object> { ["reason"] = exception.Message });
        }
    }

    private void DrainPendingJobReportArtifacts()
    {
        foreach (JobReportArtifactData artifact in pendingJobReportArtifacts.Values.ToArray())
            TryMaterializeJobReport(artifact);
    }

    private bool TryMaterializeJobReport(JobReportArtifactData artifact)
    {
        if (artifact == null || NetworkLifecycle.Instance.IsHost())
            return false;
        if (NetworkedItem.TryGet(artifact.ItemNetId, out NetworkedItem existing))
        {
            if (existing.GetTrackedItem<JobReport>() != null)
            {
                pendingJobReportArtifacts.Remove(artifact.ItemNetId);
                return true;
            }
            LogError($"JobReport artifact {artifact.ArtifactToken} conflicts with item {artifact.ItemNetId}.");
            return false;
        }
        if (!NetworkedJob.Get(artifact.JobNetId, out NetworkedJob networkedJob) || networkedJob?.Job == null)
            return false;

        Job_data jobData = artifact.Job.ToGameData();
        Debt_data debtData = artifact.Debt?.ToGameData();
        Vector3 localPosition = artifact.Position.Position + WorldMover.currentMove;
        JobReport report = BookletCreator_JobReport.Create(jobData, debtData, localPosition,
            artifact.Position.Rotation, WorldMover.OriginShiftParent);
        if (report == null)
            throw new InvalidOperationException($"BookletCreator returned null for report {artifact.ArtifactToken}.");

        NetworkedItem netItem = report.GetOrAddComponent<NetworkedItem>();
        netItem.Initialize(report, artifact.ItemNetId, false);
        networkedJob.AddReport(netItem);
        pendingJobReportArtifacts.Remove(artifact.ItemNetId);
        DebugRuntime.Publish("job", "job.report-artifact-materialized", DebugRuntimeSide.Client,
            entityType: "Item", entityId: artifact.ItemNetId.ToString(), data: new Dictionary<string, object>
            {
                ["artifactToken"] = artifact.ArtifactToken,
                ["jobNetId"] = artifact.JobNetId,
                ["gameObjectPath"] = report.gameObject.GetPath()
            });
        return true;
    }

    private void OnClientboundTaskUpdatePacket(ClientboundTaskUpdatePacket packet)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedTask.TryGet(packet.TaskNetId, out Task task) || task == null)
        {
            LogError($"Received task update for taskNetId {packet.TaskNetId}, task was not found");
            return;
        }

        task.SetState(packet.NewState);
        task.taskStartTime = packet.TaskStartTime;
        task.taskFinishTime = packet.TaskFinishTime;
    }

    private void OnClientboundJobValidateResponsePacket(ClientboundJobValidateResponsePacket packet)
    {
        Log($"Job validation response received JobNetId: {packet.JobNetId}, Status: {packet.Invalid}");

        if (!NetworkedJob.Get(packet.JobNetId, out NetworkedJob networkedJob))
            return;

        // A refused overview or booklet used to destroy the whole job here, although the host
        // still had it: the job vanished for this player. Only drop it when the host has no such job.
        networkedJob.ValidatorResponseReceived = true;
        networkedJob.ValidationAccepted = !packet.Invalid;
        if (packet.JobMissing)
            Object.Destroy(networkedJob.gameObject);
    }

    private void OnCommonPitStopInteractionPacket(CommonPitStopInteractionPacket packet)
    {
        if (!NetworkedPitStopStation.Get(packet.NetId, out var netPitStop))
        {
            LogWarning($"Pit Stop Interaction received for netId: {packet.NetId}, but pit stop does not exist!");
        }

        Log($"Pit stop interaction received for {netPitStop.StationName}");

        LogDebug(() => $"OnCommonPitStopInteractionPacket() [{netPitStop.StationName}, {packet.NetId}], interaction: [{packet.InteractionType}], resource: {packet?.ResourceType}, State: {packet.Value}");
        netPitStop.ProcessInteractionPacketAsClient(packet);
    }

    private void OnCommonPitStopPlugInteractionPacket(CommonPitStopPlugInteractionPacket packet)
    {
        if (!NetworkedPluggableObject.Get(packet.NetId, out var netPlug))
        {
            LogWarning($"Pit Stop Plug Interaction received for plug netId: {packet.NetId}, but pit stop plug does not exist!");
            return;
        }

        Log($"Pit Stop Plug Interaction received for {netPlug.NetId}");

        LogDebug(() => $"OnCommonPitStopPlugInteractionPacket() [{netPlug?.transform?.name}, {packet.NetId}], interaction: [{(PlugInteractionType)packet.InteractionType}]");
        netPlug.ProcessPacket(packet);
    }

    private void OnClientboundPitStopBulkUpdatePacket(ClientboundPitStopBulkUpdatePacket packet)
    {
        LogDebug(() => $"OnClientboundPitStopBulkUpdatePacket() NetId: {packet.NetId}, CarCount: {packet.CarCount}, CarSelection: {packet.CarSelection}, FaucetNotch: {packet.FaucetNotch}, ResourceData Count: {packet.ResourceData.Length}, PlugData: {packet.PlugData.Length}");

        if (!NetworkedPitStopStation.Get(packet.NetId, out var netPitStop))
        {
            LogWarning($"Pit Stop Bulk Data received for station netId: {packet.NetId}, but pit stop does not exist!");
            return;
        }

        Log($"Pit Stop Bulk Data received for {netPitStop.StationName}");

        netPitStop.ProcessBulkUpdate(packet);
    }

    private void OnCommonItemsBulkUpdatePacket(CommonItemsBulkUpdatePacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client);
        LogDebug(() => $"OnCommonItemsBulkUpdatePacket({packet?.Items?.Count})");

        LogDebug(() =>
        {
            string debug = "";

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

        NetworkedItemManager.Instance.ReceiveSnapshots(packet.Items);
    }

    private void OnCommonItemUpdatePacket(CommonItemUpdatePacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Item", packet?.ItemData?.ItemNetId.ToString());
        LogDebug(() => $"OnCommonItemUpdatePacket()");

        LogDebug(() =>
        {
            string debug = "";

            var item = packet.ItemData;
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

            return debug;
        });

        // Create and Destroy are representation-lifecycle operations. The bulk packet path
        // already routes them through NetworkedItemManager, but the single-item path used to
        // bypass it and call ReceiveSnapshot directly. That left Destroyed objects registered,
        // inventoried, and visible even though the packet had arrived successfully.
        if (packet.ItemData.UpdateType.HasAnyFlag(
                ItemUpdateData.ItemUpdateType.Create |
                ItemUpdateData.ItemUpdateType.Destroy))
        {
            NetworkedItemManager.Instance.ReceiveSnapshots(
                new List<ItemUpdateData> { packet.ItemData });
            return;
        }

        if (!NetworkedItem.TryGet(packet.ItemData.ItemNetId, out NetworkedItem networkedItem))
        {
            DebugTrace.Validation("item", "Item", packet.ItemData.ItemNetId.ToString(), false, "unknown-network-entity", DebugRuntimeSide.Client);
            LogWarning($"OnCommonItemUpdatePacket() Failed to find networked item for netId {packet.ItemData.ItemNetId}");
            return;
        }

        networkedItem.ReceiveSnapshot(packet.ItemData);
    }

    private void OnClientboundItemAdoptionPacket(ClientboundItemAdoptionPacket packet)
    {
        using IDisposable debugScope = DebugTrace.BeginHandler(packet, DebugRuntimeSide.Client, "Item", null);
        NetworkedItemManager.Instance.ApplyItemAdoptionResult(packet.Result);
    }

    private void OnClientboundItemRecallResultPacket(ClientboundItemRecallResultPacket packet)
    {
        Log($"[LostAndFound Recall] Recall result received: netId={packet.ItemNetId}, " +
            $"operation={packet.OperationId}, " +
            $"accepted={packet.Accepted}, revision={packet.AuthorityRevision}, " +
            $"reason={packet.RejectionReason ?? string.Empty}");
        DebugRuntime.Publish("item", packet.Accepted ? "item.recall-confirmed" : "item.recall-rejected",
            DebugRuntimeSide.Client, packet.Accepted ? DebugSeverity.Info : DebugSeverity.Warning,
            "Item", packet.ItemNetId.ToString(), new()
            {
                ["authorityRevision"] = packet.AuthorityRevision,
                ["rejectionReason"] = packet.RejectionReason ?? string.Empty
            });
        if (NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item))
            item?.CompleteLocalRecallPreparation(packet.OperationId, packet.Accepted);
    }

    private void OnClientboundItemRecallPreparePacket(ClientboundItemRecallPreparePacket packet)
    {
        string failureReason = string.Empty;
        bool succeeded = NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem item) &&
            item != null && item.TryPrepareLocalRecall(packet.OperationId, packet.RequestedSlot,
                out failureReason);
        if (item == null)
            failureReason = "missing-local-item-representation";
        SendPacketToServer(new ServerboundItemRecallPreparedPacket
        {
            OperationId = packet.OperationId,
            ItemNetId = packet.ItemNetId,
            Succeeded = succeeded,
            FailureReason = failureReason ?? string.Empty
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundLostItemsSnapshotPacket(ClientboundLostItemsSnapshotPacket packet)
    {
        int count = new[]
        {
            packet.Handles?.Length ?? 0,
            packet.NetIds?.Length ?? 0,
            packet.Revisions?.Length ?? 0,
            packet.PrefabNames?.Length ?? 0,
            packet.DisplayNames?.Length ?? 0,
            packet.Reasons?.Length ?? 0,
            packet.LostUtcTicks?.Length ?? 0
        }.Min();
        LostItemData[] items = new LostItemData[count];
        for (int i = 0; i < count; i++)
            items[i] = new LostItemData
            {
                Handle = packet.Handles[i],
                NetId = packet.NetIds[i],
                Revision = packet.Revisions[i],
                PrefabName = packet.PrefabNames[i],
                DisplayName = packet.DisplayNames[i],
                Reason = packet.Reasons[i],
                LostUtcTicks = packet.LostUtcTicks[i]
            };
        NetworkedLostAndFoundManager.ApplyClientSnapshot(packet.Generation, items);
    }

    private void OnClientboundLostItemRetrieveResultPacket(ClientboundLostItemRetrieveResultPacket packet)
    {
        Log($"[LostAndFound Recall] Lost item retrieval result received: request={packet.RequestId}, " +
            $"handle={packet.LostHandle}, accepted={packet.Accepted}, revision={packet.AuthorityRevision}, " +
            $"reason={packet.RejectionReason ?? string.Empty}");
        DebugRuntime.Publish("item", packet.Accepted ? "item.lost-and-found-recall-confirmed" :
            "item.lost-and-found-recall-rejected", DebugRuntimeSide.Client,
            packet.Accepted ? DebugSeverity.Info : DebugSeverity.Warning,
            "LostItem", packet.LostHandle.ToString(), new()
            {
                ["requestId"] = packet.RequestId,
                ["lostHandle"] = packet.LostHandle,
                ["authorityRevision"] = packet.AuthorityRevision,
                ["rejectionReason"] = packet.RejectionReason ?? string.Empty
            });
        LostItemRetrieveCompleted?.Invoke(packet);
    }

    private void OnClientboundContainerViewPacket(ClientboundContainerViewPacket packet)
    {
        ContainerViewReceived?.Invoke(packet);
    }

    private void OnClientboundContainerMutationResultPacket(
        ClientboundContainerMutationResultPacket packet)
    {
        ContainerMutationCompleted?.Invoke(packet);
    }

    private void OnCommonPaintThemePacket(CommonPaintThemePacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar netTrainCar))
            return;

        if (!PaintThemeLookup.Instance.TryGet(packet.PaintThemeId, out PaintTheme paint) || paint == null)
        {
            LogWarning($"Received paint theme change for {netTrainCar?.CurrentID}, but paint theme id '{packet.PaintThemeId}' does not exist.");
            return;
        }

        Log($"Received paint theme change for {netTrainCar?.CurrentID}, theme '{paint.AssetName}'");

        LogDebug(() => $"OnCommonPaintThemePacket() [{netTrainCar?.CurrentID}, {packet.NetId}], area: {packet.TargetArea}, paint: [{paint?.AssetName}, {packet.PaintThemeId}]");
        netTrainCar?.Common_ReceivePaintThemeUpdate(packet.TargetArea, paint);
    }

    private void OnClientboundRestorationStateChangePacket(ClientboundRestorationStateChangePacket packet)
    {
        LogDebug(() => $"OnClientboundRestorationStateChangePacket() NetId: {packet.NetId}, NewState: {packet.NewState}, TransportCarNetIds: [{string.Join(", ", packet?.TransportCarNetIds)}]");

        if (!NetworkedTrainCar.TryGet(packet.NetId, out TrainCar trainCar))
        {
            LogWarning($"Received restoration state change for netId: {packet.NetId}, but TrainCar was not found.");
            return;
        }

        Log($"Received restoration state change for {trainCar?.ID}");

        var controller = LocoRestorationController.GetForTrainCar(trainCar);
        if (controller == null)
        {
            LogWarning($"Received restoration state change for {trainCar?.ID}, but LocoRestorationController was not found.");
            return;
        }

        switch (packet.NewState)
        {
            case LocoRestorationController.RestorationState.S5_PartOrdered:
                controller.orderPartsModule.SetUnitsToBuy(0f);
                controller.OnPartsOrdered();
                break;

            case LocoRestorationController.RestorationState.S6_PartPickedUp:
                if (packet.TransportCarNetIds == null || packet.TransportCarNetIds.Length == 0)
                {
                    LogError($"Received restoration state change for {trainCar?.ID}, but transport cars are empty");
                    return;
                }

                List<Car> transportCars = [];
                foreach (var netId in packet.TransportCarNetIds)
                {
                    if (!NetworkedTrainCar.TryGet(netId, out Car transportCar) || transportCar == null)
                    {
                        LogError($"Received restoration state change for {trainCar?.ID}, but failed to find transport car with netId {netId}");
                        continue;
                    }
                    transportCars.Add(transportCar);
                }
                controller.OnOrderedPartLoadedCargo(transportCars);
                break;

            case LocoRestorationController.RestorationState.S7_PartDelivered:
                if (controller.transportingCars != null && controller.transportingCars.Count > 0)
                {
                    foreach (var transportCar in controller.transportingCars)
                    {
                        trainCar.preventFastTravelWithCar = false;
                        trainCar.preventDelete = false;
                    }
                }
                controller.transportingCars = null;
                controller.SetState(packet.NewState);
                controller.installPartsModule.AddThingToCart();
                break;

            case LocoRestorationController.RestorationState.S8_PartInstalled:
                controller.installPartsModule.SetUnitsToBuy(0f);
                controller.OnInstallPartsPaid();
                break;

            case LocoRestorationController.RestorationState.S9_LocoServiced:
                controller.OnServiceDone();
                break;

            case LocoRestorationController.RestorationState.S10_PaintJobDone:
                controller.OnPaintJobChanged();
                break;
        }
    }

    private void OnCommonCashRegisterWithModulesActionPacket(CommonCashRegisterWithModulesActionPacket packet)
    {
        if (!NetworkedCashRegisterWithModules.Get(packet.NetId, out NetworkedCashRegisterWithModules netCashRegister))
        {
            LogWarning($"Cash Register With Modules Action received for netId: {packet.NetId}, but cash register does not exist!");
            return;
        }

        Log($"Cash Register With Modules Action received for {netCashRegister.GetObjectPath()}, Action: {packet.Action}, Amount: {packet.Amount}");

        netCashRegister.Client_ProcessCashRegisterAction(packet.Action, packet.Amount);
    }

    private void OnCommonGenericSwitchStatePacket(CommonGenericSwitchStatePacket packet)
    {
        if (!NetworkedGenericSwitch.TryGet(packet.NetId, out NetworkedGenericSwitch netSwitch))
        {
            LogWarning($"Received Generic Switch State for switch {packet.NetId}, but switch does not exist!");
            return;
        }

        netSwitch.Client_ReceiveSwitchState(packet.IsOn);
    }

    #endregion

    #region Senders

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
        if (Multiplayer.Settings?.ObserverMode != true ||
            NetworkLifecycle.Instance.IsHost() || ReferenceEquals(packet, null))
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


    #region Mod Packets
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
    #endregion

    private void SendLoadStateUpdate(PlayerLoadingState newState)
    {
        Log($"Sending Load State {newState}");
        SendPacketToServer(new ServerboundLoadStateUpdatePacket { LoadState = newState }, DeliveryMethod.ReliableOrdered);
        LoadingState = newState;
    }

    public void SendPlayerPosition(PlayerTrackingData trackingData, PlayerPostureFlags posture, bool isOnCar, ushort carId, bool reliable)
    {
        //LogDebug(() => $"SendPlayerPosition({position}, {moveDir}, {rotationY}, {carId}, {isJumping}, {IsOnCar})");

        SendPacketToServer(new ServerboundPlayerPositionPacket
        {
            TrackingData = trackingData,
            Posture = posture,
            IsOnCar = isOnCar,
            CarID = carId
        }, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
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

    public void SendTimeAdvance(float amountOfTimeToSkipInSeconds, bool isSleep = false)
    {
        SendPacketToServer(new ServerboundTimeAdvancePacket
        {
            amountOfTimeToSkipInSeconds = amountOfTimeToSkipInSeconds,
            IsSleep = isSleep
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundHazmatTilesPacket(ClientboundHazmatTilesPacket packet)
    {
        HazmatSync.Client_Apply(packet.Data);
    }

    public void SendHazmatIgnite(int gridCoords, float strength)
    {
        SendPacketToServer(new ServerboundHazmatIgnitePacket { GridCoords = gridCoords, Strength = strength }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundSleepStatePacket(ClientboundSleepStatePacket packet)
    {
        BedSleepingController sleep = BedSleepingController.Instance;
        if (sleep == null)
            return;
        sleep.lastWakeTime = DateTime.FromOADate(packet.LastWakeTimeOADate);
        sleep.lastSleepDurationSeconds = packet.LastSleepDurationSeconds;
    }

    public void SendJunctionSwitched(ushort netId, byte selectedBranch, Junction.SwitchMode mode)
    {
        SendPacketToServer(new CommonChangeJunctionPacket
        {
            NetId = netId,
            SelectedBranch = selectedBranch,
            Mode = (byte)mode
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTurntableRotation(byte netId, float rotation)
    {
        SendPacketToServer(new CommonRotateTurntablePacket
        {
            NetId = netId,
            rotation = rotation
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendCouplerInteraction(CouplerInteractionType flags, Coupler coupler, Coupler otherCoupler = null)
    {
        ushort couplerNetId = coupler?.train?.GetNetId() ?? 0;
        ushort otherCouplerNetId = otherCoupler?.train?.GetNetId() ?? 0;
        bool couplerIsFront = coupler?.isFrontCoupler ?? false;
        bool otherCouplerIsFront = otherCoupler?.isFrontCoupler ?? false;

        if (couplerNetId == 0)
        {
            LogWarning($"SendCouplerInteraction failed. Coupler: {coupler.name} {couplerNetId}");
            return;
        }

        LogDebug(() => $"SendCouplerInteraction([{flags}], {coupler?.train?.ID}, {otherCoupler?.train?.ID}) coupler isFront: {couplerIsFront}, otherCoupler isFront: {otherCouplerIsFront}");

        if (coupler == null)
            return;

        if (flags.HasFlag(CouplerInteractionType.CoupleViaUI))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.Couple, coupler, otherCoupler,
                cascadeHose: flags.HasFlag(CouplerInteractionType.HoseConnect),
                cascadeMu: false, playAudio: true);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.UncoupleViaUI))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.Uncouple, coupler, null,
                cascadeHose: flags.HasFlag(CouplerInteractionType.HoseDisconnect),
                cascadeMu: true, playAudio: true);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.CouplerCouple) && otherCoupler != null)
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.Couple, coupler, otherCoupler,
                cascadeHose: false, cascadeMu: false, playAudio: false);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.CouplerLoosen))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.SetChainState, coupler, null,
                false, false, false, chainState: TrainChainState.AttachedLoose);
            return;
        }
        if (flags.HasFlag(CouplerInteractionType.CouplerTighten))
        {
            SendTrainTopologyCommand(TrainTopologyCommandKind.SetChainState, coupler, null,
                false, false, false, chainState: TrainChainState.AttachedTight);
            return;
        }

        Log($"Sending coupler interaction [{flags}] for {coupler?.train?.ID}, {(couplerIsFront ? "Front" : "Rear")}");

        SendPacketToServer(new CommonCouplerInteractionPacket
        {
            NetId = couplerNetId,
            IsFrontCoupler = couplerIsFront,
            OtherNetId = otherCouplerNetId,
            IsFrontOtherCoupler = otherCouplerIsFront,
            Flags = (ushort)flags,
        }, DeliveryMethod.ReliableOrdered);
    }


    public void SendHoseConnected(Coupler coupler, Coupler otherCoupler, bool playAudio)
    {
        if (coupler == null || otherCoupler == null)
        {
            LogWarning($"Failed to send HoseConnected, {(coupler == null ? "Coupler is null" : coupler?.train?.ID)}, {(otherCoupler == null ? "Other Coupler is null" : otherCoupler?.train?.ID)}");
            return;
        }

        ushort couplerNetId = coupler.train.GetNetId();
        ushort otherCouplerNetId = otherCoupler.train.GetNetId();

        if (couplerNetId == 0 || otherCouplerNetId == 0)
        {
            LogWarning($"SendHoseConnected failed. Coupler: {coupler?.train?.ID} {couplerNetId}, OtherCoupler: {otherCoupler?.train?.ID} {otherCouplerNetId}");
            return;
        }

        SendTrainTopologyCommand(TrainTopologyCommandKind.ConnectHose, coupler, otherCoupler,
            false, false, playAudio);
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

        SendTrainTopologyCommand(TrainTopologyCommandKind.DisconnectHose, coupler, null,
            false, false, playAudio);
    }

    public void SendMuConnected(MultipleUnitCable cable, MultipleUnitCable otherCable, bool playAudio)
    {
        ushort cableNetId = cable.muModule.train.GetNetId();
        ushort otherCableNetId = otherCable.muModule.train.GetNetId();

        if (cableNetId == 0 || otherCableNetId == 0)
        {
            LogWarning($"SendMuConnected failed. Cable: {cable.muModule.train.name} {cableNetId}, OtherCable: {otherCable.muModule.train.name} {otherCableNetId}");
            return;
        }

        Coupler coupler = cable.isFront ? cable.muModule.train.frontCoupler : cable.muModule.train.rearCoupler;
        Coupler otherCoupler = otherCable.isFront ? otherCable.muModule.train.frontCoupler : otherCable.muModule.train.rearCoupler;
        SendTrainTopologyCommand(TrainTopologyCommandKind.ConnectMultipleUnit, coupler,
            otherCoupler, false, false, playAudio);
    }

    public void SendMuDisconnected(ushort netId, MultipleUnitCable cable, bool playAudio)
    {

        Coupler coupler = cable.isFront ? cable.muModule.train.frontCoupler : cable.muModule.train.rearCoupler;
        SendTrainTopologyCommand(TrainTopologyCommandKind.DisconnectMultipleUnit, coupler,
            null, false, false, playAudio);
    }

    public void SendCockState(ushort netId, Coupler coupler, bool isOpen)
    {
        SendTrainTopologyCommand(TrainTopologyCommandKind.SetCock, coupler, null,
            false, false, false, requestedValue: isOpen);
    }

    private void OnClientboundTrainControlCommitPacket(ClientboundTrainControlCommitPacket packet)
    {
        if (!NetworkedTrainCar.TryGet(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (packet.Result != TrainControlCommitResult.Accepted)
            LogWarning($"Train control operation {packet.OperationId} for car {packet.NetId} " +
                       $"was rejected: {packet.Result}; authoritative revision {packet.Revision}");
        networkedTrainCar.Client_ReceiveControlCommit(packet);
        DebugRuntime.Publish("train", "train.control-commit-applied", DebugRuntimeSide.Client,
            entityType: "TrainCar", entityId: packet.NetId.ToString(),
            data: DebugValueSnapshotter.SnapshotObject(new
            {
                packet.NetId,
                packet.Revision,
                packet.OperationId,
                result = packet.Result.ToString(),
                portCount = packet.PortIds?.Length ?? 0,
                fuseCount = packet.FuseIds?.Length ?? 0,
                packet.IncludesHandbrake,
                packet.BrakeCylinderReleased
            }));
    }

    // Topology commands sent inside this scope come from a remote controller, used from a distance.
    private int remoteTopologyDepth;

    public IDisposable BeginRemoteTopologyCommands()
    {
        remoteTopologyDepth++;
        return new ScopeExit(() => remoteTopologyDepth--);
    }

    private sealed class ScopeExit : IDisposable
    {
        private Action onExit;
        public ScopeExit(Action onExit) => this.onExit = onExit;
        public void Dispose() { onExit?.Invoke(); onExit = null; }
    }

    private void SendTrainTopologyCommand(TrainTopologyCommandKind kind, Coupler primary,
        Coupler secondary, bool cascadeHose, bool cascadeMu, bool playAudio,
        bool requestedValue = false, TrainChainState chainState = TrainChainState.Parked)
    {
        ushort primaryNetId = primary?.train?.GetNetId() ?? 0;
        ushort secondaryNetId = secondary?.train?.GetNetId() ?? 0;
        if (primaryNetId == 0)
        {
            LogWarning($"Cannot send topology {kind}: primary endpoint has no NetId");
            return;
        }
        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Server?.CommitObservedTrainTopology();
            return;
        }
        string signature = $"{(byte)kind}:{primaryNetId}:{primary?.isFrontCoupler == true}:" +
                           $"{secondaryNetId}:{secondary?.isFrontCoupler == true}:" +
                           $"{cascadeHose}:{cascadeMu}:{requestedValue}:{(byte)chainState}";
        // Some native chain transitions raise the same completion callback twice in one
        // frame. Sending two distinct operations makes the second command stale after the
        // first advances the topology revision and can visibly undo a successful action.
        if (lastTrainTopologyCommandFrame == Time.frameCount &&
            string.Equals(lastTrainTopologyCommandSignature, signature,
                StringComparison.Ordinal))
        {
            LogDebug(() => $"Coalesced duplicate topology command {signature}");
            return;
        }
        lastTrainTopologyCommandFrame = Time.frameCount;
        lastTrainTopologyCommandSignature = signature;
        ulong sequence = ++nextTrainTopologyOperationId;
        if (sequence == 0)
            sequence = ++nextTrainTopologyOperationId;
        SendPacketToServer(new ServerboundTrainTopologyCommandPacket
        {
            OperationId = ((ulong)PlayerId << 56) | (sequence & 0x00FFFFFFFFFFFFFFUL),
            ExpectedRevision = trainTopology.Revision,
            Kind = kind,
            PrimaryCarNetId = primaryNetId,
            PrimaryEnd = primary.isFrontCoupler ? TrainEnd.Front : TrainEnd.Rear,
            HasSecondary = secondaryNetId != 0,
            SecondaryCarNetId = secondaryNetId,
            SecondaryEnd = secondary?.isFrontCoupler == true ? TrainEnd.Front : TrainEnd.Rear,
            RequestedValue = requestedValue,
            RequestedChainState = chainState,
            CascadeHose = cascadeHose,
            CascadeMultipleUnit = cascadeMu,
            PlayAudio = playAudio,
            ViaRemote = remoteTopologyDepth > 0
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendChainPlacement(Coupler coupler, TrainChainState finalState,
        bool detachMechanical)
    {
        SendTrainTopologyCommand(detachMechanical
                ? TrainTopologyCommandKind.Uncouple
                : TrainTopologyCommandKind.SetChainState,
            coupler, null, cascadeHose: false, cascadeMu: false, playAudio: false,
            chainState: finalState);
    }

    private void RequestTrainTopologySync()
    {
        // A single topology failure can defer every car's brake heartbeat. Coalesce their
        // identical repair requests instead of turning one native mismatch into hundreds
        // of reliable packets and repeated snapshots per second.
        if (Time.realtimeSinceStartup < nextTrainTopologySyncRequestAt)
            return;
        nextTrainTopologySyncRequestAt = Time.realtimeSinceStartup + 1f;
        SendPacketToServer(new ServerboundTrainTopologySyncRequestPacket
        {
            KnownBaselineId = trainTopology.BaselineId,
            KnownTopologyRevision = trainTopology.Revision
        }, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>A topology snapshot is waiting to be applied (and being retried).</summary>
    internal bool TrainTopologyPending => trainTopology.HasPending;

    internal string DescribeHostCoupling(ushort carNetId, TrainEnd end) =>
        trainTopology.DescribeEndpoint(carNetId, end);

    internal void RestoreCanonicalTrainTopology()
    {
        if (trainTopology.ReapplyCurrent(out string failure))
            return;

        LogWarning("Failed to restore canonical topology after local physics: " + failure);
        RequestTrainTopologySync();
        if (trainTopology.HasPending)
            StartTrainTopologyRetry();
    }

    public void SendBrakeCylinderReleased(ushort netId)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        SendPacketToServer(new CommonBrakeCylinderReleasePacket
        {
            OperationId = NextTrainControlOperationId(),
            ExpectedRevision = revision,
            NetId = netId
        }, DeliveryMethod.ReliableOrdered);
    }

    public ulong SendHandbrakePositionChanged(ushort netId, float position, bool viaRemote = false)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        ulong operationId = NextTrainControlOperationId();
        SendPacketToServer(new CommonHandbrakePositionPacket
        {
            OperationId = operationId,
            ExpectedRevision = revision,
            NetId = netId,
            Position = Mathf.Clamp01(position),
            ViaRemote = viaRemote
        }, DeliveryMethod.ReliableOrdered);
        return operationId;
    }

    public void SendAddCoal(ushort netId, float coalMassDelta)
    {
        SendPacketToServer(new ServerboundAddCoalPacket
        {
            NetId = netId,
            CoalMassDelta = coalMassDelta
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendTenderCoalPileInteraction(ushort netId, float coalMassDelta)
    {
        SendPacketToServer(new ServerboundTenderCoalPacket
        {
            NetId = netId,
            CoalMassDelta = coalMassDelta
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendFireboxIgnition(ushort netId)
    {
        SendPacketToServer(new ServerboundFireboxIgnitePacket
        {
            NetId = netId,
        }, DeliveryMethod.ReliableOrdered);
    }

    public ulong SendPorts(ushort netId, uint[] portIds, float[] portValues)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        ulong operationId = NextTrainControlOperationId();
        SendPacketToServer(new CommonTrainPortsPacket
        {
            OperationId = operationId,
            ExpectedRevision = revision,
            NetId = netId,
            PortIds = portIds,
            PortValues = portValues,
            Tick = NetworkLifecycle.Instance.Tick
        }, DeliveryMethod.ReliableOrdered);

        /*
        string log=$"Sending ports netId: {netId}";
        for (int i = 0; i < portIds.Length; i++) {
            log += $"\r\n\t{portIds[i]}: {portValues[i]}";
        }

        LogDebug(() => log);
        */
        return operationId;
    }

    public ulong SendFuses(ushort netId, uint[] fuseIds, bool[] fuseValues)
    {
        uint revision = NetworkedTrainCar.TryGet(netId, out NetworkedTrainCar car)
            ? car.ControlRevision : 0;
        ulong operationId = NextTrainControlOperationId();
        SendPacketToServer(new CommonTrainFusesPacket
        {
            OperationId = operationId,
            ExpectedRevision = revision,
            NetId = netId,
            FuseIds = fuseIds,
            FuseValues = fuseValues
        }, DeliveryMethod.ReliableOrdered);
        return operationId;
    }

    /// <summary>True when a train control operation id was issued by this client.</summary>
    public bool IsOwnTrainControlOperation(ulong operationId) =>
        operationId != 0 && (byte)(operationId >> 56) == PlayerId;

    private ulong NextTrainControlOperationId()
    {
        ulong sequence = ++nextTrainControlOperationId;
        if (sequence == 0)
            sequence = ++nextTrainControlOperationId;
        return ((ulong)PlayerId << 56) | (sequence & 0x00FFFFFFFFFFFFFFUL);
    }

    public void SendSandboxCargo(ushort carNetId, uint cargoTypeNetId)
    {
        SendPacketToServer(new ServerboundSandboxCargoPacket { CarNetId = carNetId, CargoTypeNetId = cargoTypeNetId },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendSandboxCarDamage(ushort carNetId, byte damageLevel, bool derail)
    {
        SendPacketToServer(new ServerboundSandboxCarDamagePacket { CarNetId = carNetId, DamageLevel = damageLevel, Derail = derail },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendCargoIgnite(ushort carNetId, byte index, float strength, bool explode)
    {
        SendPacketToServer(new ServerboundCargoIgnitePacket { CarNetId = carNetId, Index = index, Strength = strength, Explode = explode },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendTrainSyncRequest(ushort netId)
    {
        SendPacketToServer(new ServerboundTrainSyncRequestPacket
        {
            NetId = netId
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainDeleteRequest(ushort netId)
    {
        SendPacketToServer(new ServerboundTrainDeleteRequestPacket
        {
            NetId = netId
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainRerailRequest(ushort netId, ushort trackId, Vector3 position, Vector3 forward)
    {
        SendPacketToServer(new ServerboundTrainRerailRequestPacket
        {
            NetId = netId,
            TrackId = trackId,
            Position = position,
            Forward = forward
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendTrainSpawnRequest(string liveryId, ushort trackNetId, int position, bool withTrackDirection)
    {
        SendPacketToServer(new ServerboundTrainSpawnRequestPacket
        {
            LiveryId = liveryId,
            TrackNetId = trackNetId,
            Index = position,
            WithTrackDirection = withTrackDirection
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendWorkTrainRequest(uint ticketId, string liveryId, ushort trackNetId, int position, bool withTrackDirection)
    {
        SendPacketToServer(new ServerboundWorkTrainRequestPacket
        {
            TicketId = ticketId,
            LiveryId = liveryId,
            TrackNetId = trackNetId,
            Index = position,
            WithTrackDirection = withTrackDirection
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendLicensePurchaseRequest(string id, bool isJobLicense)
    {
        SendPacketToServer(new ServerboundLicensePurchaseRequestPacket
        {
            Id = id,
            IsJobLicense = isJobLicense
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendPayFeesRequest()
    {
        SendPacketToServer(new ServerboundPayFeesRequestPacket(), DeliveryMethod.ReliableUnordered);
    }

    public void SendJobValidateRequest(NetworkedJob job, NetworkedStationController station,
        NetworkedItem validationItem)
    {
        SendPacketToServer(new ServerboundJobValidateRequestPacket
        {
            JobNetId = job.NetId,
            StationNetId = station.NetId,
            ItemNetId = validationItem?.NetId ?? 0,
            validationType = job.ValidationType
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendWarehouseRequest(WarehouseAction action, ushort netId)
    {
        SendPacketToServer(new ServerboundWarehouseMachineControllerRequestPacket
        {
            NetId = netId,
            WarehouseAction = action,
        }, DeliveryMethod.ReliableUnordered);
    }

    public void SendChat(string message)
    {
        SendPacketToServer(new CommonChatPacket
        {
            message = message
        }, DeliveryMethod.ReliableUnordered);

    }

    public void SendPitStopInteractionPacket(ushort netId, PitStopStationInteractionType interaction, ResourceType? resource, float state)
    {
        LogDebug(() => $"SendPitStopInteractionPacket({netId}, [{interaction}], {resource}, {state})");

        int res = resource == null ? 0 : (int)resource;
        SendPacketToServer(new CommonPitStopInteractionPacket
        {
            Tick = NetworkLifecycle.Instance.Tick,
            NetId = netId,
            InteractionType = interaction,
            ResourceType = res,
            Value = state
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendPitStopPlugInteractionPacket
    (
        ushort netId,
        PlugInteractionType interaction,
        Vector3? position = null,
        Quaternion? rotation = null,
        ushort trainCarNetId = 0,
        sbyte socketIndex = -1
    )
    {
        LogDebug(() => $"SendPitStopPlugInteractionPacket({netId}, {interaction}, pos: {position}, rot: {rotation}, trainNetId: {trainCarNetId}, socketIndex: {socketIndex})");

        SendNetSerializablePacketToServer(new CommonPitStopPlugInteractionPacket
        {
            NetId = netId,
            InteractionType = interaction,
            TrainCarNetId = trainCarNetId,
            SocketIndex = socketIndex,
            Position = position,
            Rotation = rotation,

        }, DeliveryMethod.ReliableOrdered);
    }

    //public void SendItemsBulkUpdatePacket(List<ItemUpdateData> items)
    //{
    //    Log($"Sending CommonItemsBulkUpdatePacket with {items.Count()} items");

    //    SendNetSerializablePacketToServer(new CommonItemsBulkUpdatePacket { Items = items },
    //            DeliveryMethod.ReliableOrdered);
    //}

    public void SendItemUpdatePacket(ItemUpdateData updateData)
    {
        if (updateData == null || updateData.ItemNetId == 0)
        {
            LogWarning($"Blocked local item snapshot with no host-issued network ID. UpdateType: {updateData?.UpdateType}, prefab: {updateData?.PrefabName}");
            DebugRuntime.Publish("item", "item.zero-id-send-blocked", DebugRuntimeSide.Client, DebugSeverity.Error,
                "Item", "0", DebugTrace.ItemSnapshotData(updateData));
            return;
        }

        DebugRuntime.Publish("item", "item.packet-send-requested", DebugRuntimeSide.Client, entityType: "Item",
            entityId: updateData?.ItemNetId.ToString(), data: DebugTrace.ItemSnapshotData(updateData));
        LogDebug(()=>
        {
            string debug = $"SendItemUpdatePacket() UpdateType: {updateData?.UpdateType}, itemNetId: {updateData?.ItemNetId}, PrefabName: {updateData?.PrefabName}, Equipped: {updateData?.ItemState}, Position: {updateData?.ItemPosition}, Rotation: {updateData?.ItemRotation}, ThrowDirection: {updateData?.ThrowDirection}, Player: {updateData?.PlayerId}, CarNetId: {updateData?.CarNetId}, AttachedFront: {updateData?.AttachedFront}";
            debug += $"\r\nStates: {updateData?.States?.Count}";
            if (updateData.States != null)
                foreach (var state in updateData?.States)
                    debug += $"\r\n\t{state.Key}: {state.Value}";
            return debug;
        });

        SendPacketToServer(new CommonItemUpdatePacket { ItemData = updateData },
                DeliveryMethod.ReliableOrdered);
    }

    private readonly Dictionary<ushort, uint> trainsetRelocationRevisions = new();
    private readonly Dictionary<ushort, uint> pendingTrainsetRelocationRevisions = new();

    private void OnClientboundTrainsetRelocationPacket(ClientboundTrainsetRelocationPacket packet)
    {
        if (packet == null || packet.RootNetId == 0 || packet.Cars == null) return;
        if (trainsetRelocationRevisions.TryGetValue(packet.RootNetId, out uint applied) &&
            packet.Revision <= applied)
        {
            LogDebug(() => $"Ignoring stale trainset relocation {packet.OperationId} " +
                $"revision {packet.Revision}, applied {applied}");
            SendTrainsetRelocationAck(packet, 1, "stale-ignored", applied);
            return;
        }
        if (pendingTrainsetRelocationRevisions.TryGetValue(packet.RootNetId, out uint pending) &&
            packet.Revision <= pending)
        {
            SendTrainsetRelocationAck(packet, 1, "pending-newer-or-equal", pending);
            return;
        }
        pendingTrainsetRelocationRevisions[packet.RootNetId] = packet.Revision;
        SingletonBehaviour<CoroutineManager>.Instance.Run(ApplyTrainsetRelocation(packet));
    }

    private IEnumerator ApplyTrainsetRelocation(ClientboundTrainsetRelocationPacket packet)
    {
        float deadline = Time.realtimeSinceStartup + 10f;
        string failure;
        while (!NetworkedCarSpawner.TryApplyRelocation(packet.Cars, packet.HostTick, out failure))
        {
            if (!pendingTrainsetRelocationRevisions.TryGetValue(packet.RootNetId,
                    out uint pending) || pending != packet.Revision)
            {
                SendTrainsetRelocationAck(packet, 1, "superseded-before-apply", pending);
                yield break;
            }
            bool dependency = failure.StartsWith("missing-car:", StringComparison.Ordinal) ||
                failure.StartsWith("missing-track:", StringComparison.Ordinal);
            if (!dependency || Time.realtimeSinceStartup >= deadline)
            {
                LogError($"Trainset relocation {packet.OperationId} could not be applied: {failure}");
                SendTrainsetRelocationAck(packet, 2,
                    dependency ? "deferred-dependency-timeout:" + failure : failure, 0);
                if (pendingTrainsetRelocationRevisions.TryGetValue(packet.RootNetId,
                        out uint current) && current == packet.Revision)
                    pendingTrainsetRelocationRevisions.Remove(packet.RootNetId);
                yield break;
            }
            yield return null;
        }
        trainsetRelocationRevisions[packet.RootNetId] = packet.Revision;
        pendingTrainsetRelocationRevisions.Remove(packet.RootNetId);
        uint appliedHash = TrainsetRelocationHash.Compute(packet.Cars);
        DebugRuntime.Publish("train-relocation", "train-relocation.client-applied",
            DebugRuntimeSide.Client, entityId: "train:" + packet.RootNetId,
            correlationId: packet.OperationId, data: new()
            {
                ["revision"] = packet.Revision, ["carCount"] = packet.Cars.Length,
                ["hostTick"] = packet.HostTick
            });
        if (packet.CommittedHash != 0 && packet.CommittedHash != appliedHash)
            SendTrainsetRelocationAck(packet, 3, "hash-mismatch", appliedHash);
        else
            SendTrainsetRelocationAck(packet, 0, "applied", appliedHash);
    }

    private void SendTrainsetRelocationAck(ClientboundTrainsetRelocationPacket packet,
        byte status, string reason, uint hash)
    {
        SendPacketToServer(new ServerboundTrainsetRelocationAckPacket
        {
            OperationId = packet.OperationId, Revision = packet.Revision,
            RootNetId = packet.RootNetId, Status = status,
            ReasonCode = reason ?? string.Empty, AppliedHash = hash
        }, DeliveryMethod.ReliableOrdered);
    }

    internal void SendWorldItemProjectionAck(ushort netId, uint revision, bool projected)
    {
        SendPacketToServer(new ServerboundWorldItemProjectionAckPacket
        {
            ItemNetId = netId,
            AuthorityRevision = revision,
            Projected = projected
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobBookletSummonRequest(uint stationNetId)
    {
        SendPacketToServer(new ServerboundJobBookletSummonRequestPacket
        {
            StationNetId = stationNetId
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendItemAdoption(ItemAdoptionRequestData request)
    {
        // TEMPORARY compatibility only. This is not a general client creation API.
        SendPacketToServer(new ServerboundItemAdoptionPacket
        {
            Request = request
        }, DeliveryMethod.ReliableOrdered);
    }

    public void RequestLostItems(uint requestId)
    {
        SendPacketToServer(new ServerboundLostItemsRequestPacket { RequestId = requestId },
            DeliveryMethod.ReliableOrdered);
    }

    public void RequestContainerView(uint requestId, ushort shellNetId,
        uint containerHandle = 0, int offset = 0, byte count = 64)
    {
        SendPacketToServer(new ServerboundContainerBrowsePacket
        {
            RequestId = requestId,
            ShellNetId = shellNetId,
            ContainerHandle = containerHandle,
            Offset = offset,
            Count = count
        }, DeliveryMethod.ReliableOrdered);
    }

    public void RequestContainerMutation(ServerboundContainerMutationPacket packet)
    {
        if (packet == null) return;
        SendPacketToServer(packet, DeliveryMethod.ReliableOrdered);
    }

    public void RequestLostItemRetrieval(uint requestId, uint lostHandle, ushort currentNetId,
        uint expectedRevision, int requestedSlot = -1)
    {
        Inventory inventory = Inventory.Instance;
        int capacity = inventory?.Capacity ?? 0;
        int existingItemSlot = -1;
        if (NetworkedItem.TryGet(currentNetId, out NetworkedItem existingItem) && existingItem != null)
            existingItemSlot = inventory?.IndexOf(existingItem.gameObject) ?? -1;
        List<int> occupied = new();
        for (int slot = 0; slot < capacity; slot++)
            if (inventory.PeekItemAtSlot(slot, true) != null)
                occupied.Add(slot);
        Log($"[LostAndFound Recall] Preparing retrieval packet: request={requestId}, " +
            $"handle={lostHandle}, currentNetId={currentNetId}, " +
            $"revision={expectedRevision}, requestedSlot={requestedSlot}, existingSlot={existingItemSlot}, " +
            $"capacity={capacity}, occupied=[{string.Join(",", occupied)}]");
        SendPacketToServer(new ServerboundLostItemRetrievePacket
        {
            RequestId = requestId,
            LostHandle = lostHandle,
            ExpectedRevision = expectedRevision,
            RequestedSlot = requestedSlot,
            ExistingItemSlot = existingItemSlot,
            InventoryCapacity = capacity,
            OccupiedSlots = occupied.ToArray()
        }, DeliveryMethod.ReliableOrdered);
    }

    private void SendWorldItemCatalogue()
    {
        var summary = NetworkedItemManager.Instance.GetAuthoredCatalogueSummary();
        SendPacketToServer(new ServerboundWorldItemCataloguePacket
        {
            ItemCount = summary.Count,
            CollisionCount = summary.Collisions,
            Digest = summary.Digest
        }, DeliveryMethod.ReliableOrdered);
    }

    private void OnClientboundWorldItemCataloguePacket(ClientboundWorldItemCataloguePacket packet)
    {
        NetworkedItemManager.Instance.SetAuthoredCatalogueNegotiationResult(packet?.Accepted == true);
        DebugRuntime.Publish("item-world", "world-item.catalogue-result", DebugRuntimeSide.Client,
            packet?.Accepted == true ? DebugSeverity.Info : DebugSeverity.Error, data: new()
            {
                ["accepted"] = packet?.Accepted == true,
                ["hostCount"] = packet?.HostItemCount ?? 0,
                ["hostCollisionCount"] = packet?.HostCollisionCount ?? 0,
                ["hostDigest"] = packet?.HostDigest ?? string.Empty,
                ["reason"] = packet?.Reason ?? "missing-response"
            });
    }

    private void OnClientboundItemSpatialLeasePacket(ClientboundItemSpatialLeasePacket packet) =>
        NetworkedItemManager.Instance?.ReceiveItemSpatialLease(packet);

    private void OnClientboundItemSpatialSamplePacket(ClientboundItemSpatialSamplePacket packet)
    {
        if (packet?.State != null)
            NetworkedItemManager.Instance?.ReceiveItemSpatialSample(packet.State);
    }

    private void OnClientboundItemSpatialCommitPacket(ClientboundItemSpatialCommitPacket packet) =>
        NetworkedItemManager.Instance?.ReceiveItemSpatialCommit(packet);

    internal void SendItemSpatialSample(ItemSpatialStateData state)
    {
        if (state == null) return;
        SendPacketToServer(new ServerboundItemSpatialSamplePacket { State = state },
            DeliveryMethod.Unreliable);
    }

    internal void SendItemSpatialSettlement(ItemSpatialStateData state)
    {
        if (state == null) return;
        SendPacketToServer(new ServerboundItemSpatialSettlementPacket { State = state },
            DeliveryMethod.ReliableOrdered);
    }

    internal void SendItemTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet)
    {
        if (packet == null) return;
        SendPacketToServer(packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendItemRecall(ushort itemNetId, uint expectedRevision, int requestedSlot)
    {
        SendPacketToServer(new ServerboundItemRecallPacket
        {
            ItemNetId = itemNetId,
            ExpectedRevision = expectedRevision,
            RequestedSlot = requestedSlot
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendItemBelowMapReport(ushort itemNetId, float surfaceHeight)
    {
        SendPacketToServer(new ServerboundItemBelowMapPacket { ItemNetId = itemNetId, SurfaceHeight = surfaceHeight },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendMoneyItemDeposit(ushort itemNetId)
    {
        SendPacketToServer(new ServerboundMoneyItemDepositPacket { ItemNetId = itemNetId },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendShopPurchase(int shopIndex, string itemPrefabName, int amount)
    {
        SendPacketToServer(new ServerboundShopPurchasePacket
        {
            ShopIndex = shopIndex,
            ItemPrefabName = itemPrefabName,
            Amount = amount
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendShopPayment(double amount)
    {
        SendPacketToServer(new ServerboundShopPaymentPacket { Amount = amount },
            DeliveryMethod.ReliableOrdered);
    }

    public void SendPaintThemeChange(NetworkedTrainCar netTraincar, TrainCarPaint.Target targetArea, uint themeId)
    {
        Log($"Sending paint theme change for {netTraincar.CurrentID}");

        SendPacketToServer(new CommonPaintThemePacket { NetId = netTraincar.NetId, TargetArea = targetArea, PaintThemeId = themeId }, DeliveryMethod.ReliableUnordered);
    }

    public void SendCashRegisterAction(ushort netId, CashRegisterAction action, double amount = 0.0f)
    {
        SendPacketToServer(
            new CommonCashRegisterWithModulesActionPacket
            {
                NetId = netId,
                Action = action,
                Amount = amount
            },
            DeliveryMethod.ReliableOrdered
        );
    }

    public void SendTrainControlAuthorityRequest(ushort netId, uint portNetId, bool requestAuthority)
    {
        SendPacketToServer
        (
            new ServerboundTrainControlAuthorityPacket
            {
                NetId = netId,
                PortNetId = portNetId,
                RequestAuthority = requestAuthority
            },
            DeliveryMethod.ReliableOrdered
        );
    }

    public void SendGenericSwitchState(uint netId, bool isOn)
    {
        SendPacketToServer
        (
            new CommonGenericSwitchStatePacket
            {
                NetId = netId,
                IsOn = isOn
            },
            deliveryMethod: DeliveryMethod.ReliableOrdered
        );
    }
    #endregion
}
