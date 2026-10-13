using Multiplayer.Core.Trains;
using Multiplayer.Networking.Data.Train;
using Multiplayer.Networking.Packets.Clientbound.Train;
using System;
using System.Linq;

namespace Multiplayer.Components.Networking.Train;

internal sealed class TrainTopologyReconciler
{
    private ClientboundTrainTopologySnapshotPacket pending;
    private ClientboundTrainTopologySnapshotPacket lastApplied;
    private bool baselineSnapshotReceived;

    internal uint BaselineId { get; private set; }
    internal uint Revision { get; private set; }
    internal bool BaselineApplied { get; private set; }
    internal bool HasPending => pending != null;

    internal void Reset(uint baselineId)
    {
        BaselineId = baselineId;
        Revision = 0;
        BaselineApplied = false;
        baselineSnapshotReceived = false;
        pending = null;
        lastApplied = null;
    }

    internal bool Receive(ClientboundTrainTopologySnapshotPacket packet, out string failure)
    {
        failure = string.Empty;
        if (packet == null)
        {
            failure = "topology-snapshot-null";
            return false;
        }
        if (packet.BaselineId != 0 && BaselineId != 0 && packet.BaselineId != BaselineId)
        {
            failure = $"topology-baseline-mismatch:{packet.BaselineId}!={BaselineId}";
            return false;
        }
        if (packet.TopologyRevision < Revision)
            return true;

        TrainEndpointStateData[] endpointData =
            packet.Endpoints ?? Array.Empty<TrainEndpointStateData>();
        if (endpointData.Any(endpoint => endpoint == null) ||
            endpointData.GroupBy(endpoint => (endpoint.CarNetId, endpoint.End))
                .Any(group => group.Count() != 1))
        {
            failure = "topology-endpoint-identity-invalid";
            return false;
        }
        BrakeLineStateData[] brakeLines = packet.BrakeLines ??
            Array.Empty<BrakeLineStateData>();
        if (brakeLines.Any(line => line == null || line.AnchorCarNetId == 0 ||
                float.IsNaN(line.PipePressure) || float.IsInfinity(line.PipePressure)) ||
            brakeLines.GroupBy(line => line.AnchorCarNetId).Any(group => group.Count() != 1))
        {
            failure = "brake-line-shape-invalid";
            return false;
        }
        TrainControlStateData[] controlStates = packet.ControlStates ??
            Array.Empty<TrainControlStateData>();
        TrainControlRevisionData[] controlRevisions =
            packet.ControlRevisions ?? Array.Empty<TrainControlRevisionData>();
        if (controlRevisions.Any(control => control == null || control.CarNetId == 0) ||
            controlRevisions.GroupBy(control => control.CarNetId)
                .Any(group => group.Count() != 1) ||
            controlStates.GroupBy(control => control?.CarNetId ?? (ushort)0)
                .Any(group => group.Count() != 1) ||
            controlStates.Any(control => control == null || control.CarNetId == 0 ||
                control.PortIds == null || control.PortValues == null ||
                control.PortIds.Length != control.PortValues.Length ||
                control.FuseIds == null || control.FuseValues == null ||
                control.FuseIds.Length != control.FuseValues.Length ||
                control.PortValues.Any(value => float.IsNaN(value) || float.IsInfinity(value)) ||
                float.IsNaN(control.HandbrakePosition) ||
                float.IsInfinity(control.HandbrakePosition)))
        {
            failure = "train-control-state-shape-invalid";
            return false;
        }
        var state = new TrainTopologyState(packet.TopologyRevision,
            endpointData.Select(endpoint => endpoint.ToCore()));
        if (!TrainTopologyStateMachine.Validate(state, out failure))
            return false;
        if (packet.BaselineId != 0)
            baselineSnapshotReceived = true;
        foreach (ushort carNetId in endpointData.Select(endpoint => endpoint.CarNetId).Distinct())
        {
            if (!NetworkedTrainCar.TryGetQuiet(carNetId, out NetworkedTrainCar car) ||
                car?.Client_Initialized != true)
            {
                pending = packet;
                failure = "endpoint-not-ready:" + carNetId;
                return false;
            }
        }
        if (!TrainTopologyNativeAdapter.TryReconcile(state, brakeLines, out failure))
        {
            pending = packet;
            return false;
        }
        foreach (TrainControlRevisionData control in controlRevisions)
            if (NetworkedTrainCar.TryGet(control.CarNetId, out NetworkedTrainCar car))
                car.Client_SetControlRevision(control.Revision);
        foreach (TrainControlStateData control in controlStates)
            if (NetworkedTrainCar.TryGet(control.CarNetId, out NetworkedTrainCar car))
                car.Client_ReceiveControlState(control);

        pending = null;
        lastApplied = packet;
        Revision = packet.TopologyRevision;
        if (packet.BaselineId != 0)
        {
            BaselineId = packet.BaselineId;
            BaselineApplied = true;
        }
        else if (BaselineId != 0 && baselineSnapshotReceived)
            BaselineApplied = true;
        return true;
    }

    internal bool ReapplyCurrent(out string failure)
    {
        failure = string.Empty;
        if (lastApplied == null)
            return true;

        // A topology replay repairs native joints changed by TrainCar.Derail. Do not run
        // Receive here: its bundled control snapshot may predate current throttle,
        // handbrake, or fuse commits and would roll unrelated cabin state backwards.
        var state = new TrainTopologyState(lastApplied.TopologyRevision,
            (lastApplied.Endpoints ?? Array.Empty<TrainEndpointStateData>())
                .Select(endpoint => endpoint.ToCore()));
        return TrainTopologyNativeAdapter.TryReconcile(state,
            lastApplied.BrakeLines ?? Array.Empty<BrakeLineStateData>(), out failure);
    }

    internal bool TryApplyPending(out string failure)
    {
        failure = string.Empty;
        return pending == null || Receive(pending, out failure);
    }
}
