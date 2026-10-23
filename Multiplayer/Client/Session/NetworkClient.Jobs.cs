using DV.Booklets;
using DV.Logic.Job;
using LiteNetLib;
using Multiplayer.Client.Jobs;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Protocol.Data.World;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Client.Session;

// NetworkClient's jobs handlers and sends (see docs/design/code-layout.md).
public partial class NetworkClient : IJobsClient
{
    private void OnClientboundWarehouseControllerUpdatePacket(ClientboundWarehouseControllerUpdatePacket packet)
    {
        LogDebug(() => $"OnClientboundWarehouseControllerUpdatePacket() NetId: {packet.NetId}, IsLoading: {packet.IsLoading}, JobNetId: {packet.JobNetId}, CarNetId: {packet.CarNetId}, CargoType: {packet.CargoTypeNetId}, Preset: [{(WarehouseMachineController.TextPreset)packet.Preset}, {packet.Preset}]");
        if (!NetworkedWarehouseMachineController.Get(packet.NetId, out NetworkedWarehouseMachineController networkedWarehouseMachineController))
        {
            LogWarning($"OnClientboundWarehouseControllerUpdatePacket() Failed to find networked warehouse machine controller for [{packet.NetId}]");
            return;
        }

        WarehouseMachineScreen.ProcessUpdate(networkedWarehouseMachineController, packet);
    }

    private void OnClientboundJobsCreatePacket(ClientboundJobsCreatePacket packet)
    {
        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController))
        {
            LogError($"OnClientboundJobsCreatePacket() {packet.StationNetId} does not exist!");
            return;
        }

        Log($"Received {packet.Jobs.Length} jobs for station {networkedStationController.StationController.logicStation.ID}");

        ClientStationJobs.AddJobs(networkedStationController, packet.Jobs);
        DrainPendingJobReportArtifacts();
    }

    private void OnClientboundJobsUpdatePacket(ClientboundJobsUpdatePacket packet)
    {
        if (!NetworkedStationController.Get(packet.StationNetId, out NetworkedStationController networkedStationController))
        {
            LogError($"OnClientboundJobsUpdatePacket() {packet.StationNetId} does not exist!");
            return;
        }

        Log($"Received {packet.JobUpdates.Length} job updates for station {networkedStationController.StationController.logicStation.ID}");

        ClientStationJobs.UpdateJobs(networkedStationController, packet.JobUpdates);
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
        if (artifact == null)
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

    public void SendJobBookletSummonRequest(uint stationNetId)
    {
        SendPacketToServer(new ServerboundJobBookletSummonRequestPacket
        {
            StationNetId = stationNetId
        }, DeliveryMethod.ReliableOrdered);
    }

    public void SendJobRerollRequest(ushort jobNetId, ushort itemNetId, Vector3 binPosition)
    {
        SendPacketToServer(new ServerboundJobRerollRequestPacket
        {
            JobNetId = jobNetId,
            ItemNetId = itemNetId,
            BinPosition = binPosition
        }, DeliveryMethod.ReliableOrdered);
    }
}
