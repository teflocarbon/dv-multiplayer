using DV.Logic.Job;
using LiteNetLib;
using Multiplayer.Server.Jobs;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;
using Multiplayer.Shared.Transport;
using Multiplayer.Debugging.Protocol;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Session;

// NetworkServer's jobs handlers and sends (see docs/design/code-layout.md).
public partial class NetworkServer
{
    partial void RegisterLocalJobs()
    {
        HandleLocal<ServerboundJobBookletSummonRequestPacket>(HandleJobBookletSummon);
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

    private void OnServerboundJobBookletSummonRequestPacket(
        ServerboundJobBookletSummonRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            HandleJobBookletSummon(packet, player);
    }

    // The validator's reprint button, from any player; the host's own comes in-process.
    private void HandleJobBookletSummon(ServerboundJobBookletSummonRequestPacket packet, ServerPlayer player)
    {
        if (!NetworkedStationController.Get(packet.StationNetId,
                out NetworkedStationController station) || station.JobValidator == null)
        {
            LogWarning($"Job booklet summon station not found: {packet.StationNetId}");
            return;
        }
        JobBookletSummonCoordinator.TryStart(station.JobValidator, player);
    }
}
