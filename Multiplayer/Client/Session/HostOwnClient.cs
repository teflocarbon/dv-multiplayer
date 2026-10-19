using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Clientbound.SaveGame;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Protocol.Packets.Common.Train;
using System;
using System.Collections.Generic;

namespace Multiplayer.Client.Session;

/// <summary>
/// What the host's own client does with each packet a client handles. That client shares the
/// server's world, so it never follows the host's state: applying it there makes the authority
/// follow itself, a copy at best and an old one at worst. (A topology snapshot sent back to it
/// uncoupled every consist the host spawned, 2026-10-19.) See "The host's own client" in
/// docs/architecture.md.
/// </summary>
public static class HostOwnClient
{
    // The host's state, sent for players to follow. The host's client drops these, whoever sent them.
    private static readonly HashSet<Type> ignored = new()
    {
        // Joining: the host has its world already and skips these steps.
        typeof(ClientboundLoadStateInfoPacket),
        typeof(ClientboundGameParamsPacket),
        typeof(ClientboundSaveGameDataPacket),
        typeof(ClientboundRailwayStatePacket),
        typeof(ClientboundCustomizationsPacket),
        typeof(ClientboundTickSyncPacket),

        // World and career
        typeof(ClientboundWeatherPacket),
        typeof(ClientboundSleepStatePacket),
        typeof(ClientboundHazmatTilesPacket),
        typeof(ClientboundShopStockPacket),
        typeof(ClientboundMoneyPacket),
        typeof(ClientboundLicenseAcquiredPacket),
        typeof(ClientboundGarageUnlockPacket),
        typeof(ClientboundDebtStatusPacket),
        // Other players' junctions, turntables and time skips: the server applies them to its
        // world itself (this client used to be what applied them), and doesn't send them here.
        typeof(ClientboundTimeAdvancePacket),
        typeof(CommonChangeJunctionPacket),
        typeof(CommonRotateTurntablePacket),

        // Rolling stock
        typeof(ClientboundSpawnTrainSetPacket),
        typeof(ClientboundTrainBaselineBeginPacket),
        typeof(ClientboundTrainBaselineCompletePacket),
        typeof(ClientboundTrainTopologySnapshotPacket),
        typeof(ClientboundDestroyTrainCarPacket),
        typeof(ClientboundTrainDerailCausePacket),
        typeof(ClientboundRerailTrainPacket),
        typeof(ClientboundMoveTrainPacket),
        typeof(ClientboundTrainsetRelocationPacket),
        typeof(ClientboundTrainsetPhysicsPacket),
        typeof(ClientboundBrakeStateUpdatePacket),
        typeof(ClientboundTrainControlCommitPacket),
        typeof(CommonCouplerInteractionPacket),
        typeof(CommonPaintThemePacket),
        typeof(ClientboundRestorationStateChangePacket),
        typeof(ClientboundCargoStatePacket),
        typeof(ClientboundExplosionPacket),
        typeof(ClientboundCargoFirePacket),
        typeof(ClientboundCargoHealthUpdatePacket),
        typeof(ClientboundCarHealthUpdatePacket),
        typeof(ClientboundWarehouseControllerUpdatePacket),
        typeof(ClientboundWindowsBrokenPacket),
        typeof(ClientboundWindowsRepairedPacket),

        // Jobs
        typeof(ClientboundJobsCreatePacket),
        typeof(ClientboundJobsUpdatePacket),
        typeof(ClientboundTaskUpdatePacket),
        typeof(ClientboundJobReportArtifactPacket),
        typeof(ClientboundJobValidateResponsePacket),

        // Items: the host's items are its own records, and it recalls, adopts and simulates them
        // in-process. The spatial lease and commit used to come back to it for items it simulated,
        // replacing the server's own lease a round trip late (and, for a transferred or train-woken
        // lease, setting the item's velocity back to where it started).
        typeof(CommonItemsBulkUpdatePacket),
        typeof(CommonItemUpdatePacket),
        typeof(ClientboundItemAdoptionPacket),
        typeof(ClientboundItemRecallPreparePacket),
        typeof(ClientboundItemRecallResultPacket),
        typeof(ClientboundWorldItemCataloguePacket),
        typeof(ClientboundItemSpatialLeasePacket),
        typeof(ClientboundItemSpatialSamplePacket),
        typeof(ClientboundItemSpatialCommitPacket),

        // Pit stops, registers and switches. A refused register request used to come back to the
        // host and empty the register's modules there.
        typeof(CommonPitStopInteractionPacket),
        typeof(CommonPitStopPlugInteractionPacket),
        typeof(ClientboundPitStopBulkUpdatePacket),
        typeof(CommonCashRegisterWithModulesActionPacket),
        typeof(CommonGenericSwitchStatePacket),
    };

    // Applied like on any client: the host player's own session and the other players.
    private static readonly HashSet<Type> applied = new()
    {
        typeof(ClientboundLoginResponsePacket),
        typeof(ClientboundServerLoadingPacket),
        typeof(ClientboundDisconnectPacket),
        typeof(ClientboundRemoveLoadingScreenPacket),
        typeof(ClientboundRpcResponsePacket),
        typeof(ClientboundPingUpdatePacket),
        typeof(ClientboundPlayerJoinedPacket),
        typeof(ClientboundPlayerDisconnectPacket),
        typeof(ClientboundPlayerPositionPacket),
        typeof(ClientboundPlayerPreferencesUpdatePacket),
        typeof(CommonChatPacket),
        // Blocks the host player's controls while another player holds them.
        typeof(ClientboundTrainControlAuthorityUpdatePacket),
        // Answers to the host player's career screen and container UI: views, not world state.
        typeof(ClientboundLostItemsSnapshotPacket),
        typeof(ClientboundLostItemRetrieveResultPacket),
        typeof(ClientboundContainerViewPacket),
        typeof(ClientboundContainerMutationResultPacket),
    };

    /// <summary>Packets the host's own client drops: the host's state, sent for players to follow.</summary>
    public static IReadOnlyCollection<Type> IgnoredPacketTypes => ignored;

    /// <summary>Packets the host's own client applies like any client.</summary>
    public static IReadOnlyCollection<Type> AppliedPacketTypes => applied;

    public static bool Ignores(Type packetType) => ignored.Contains(packetType);

    public static bool Classifies(Type packetType) => ignored.Contains(packetType) || applied.Contains(packetType);
}
