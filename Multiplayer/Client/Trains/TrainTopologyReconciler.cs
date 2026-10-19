using Multiplayer.Core.Trains;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Trains;
using System;
using System.Linq;

namespace Multiplayer.Client.Trains;

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
        // Once joined, a coupling between cars still metres apart here waits for the cars to come
        // together (TrainTopologyNativeAdapter.MaxCoupleDistance). Everything else in the snapshot
        // is applied and it counts as the current topology, so a replay never rolls back to an
        // older one; it stays pending until the retry makes the coupling. The retry only couples:
        // the snapshot's brake pipe pressures and controls went in the first time, and applying
        // them again would roll back what has changed since.
        bool retry = packet == lastApplied;
        bool deferred = false;
        if (!TrainTopologyNativeAdapter.TryReconcile(state,
                retry ? Array.Empty<BrakeLineStateData>() : brakeLines, out failure,
                deferDistantCouplings: BaselineApplied))
        {
            pending = packet;
            if (!failure.StartsWith(TrainTopologyNativeAdapter.CoupleDeferredFailure))
                return false;
            deferred = true;
        }
        if (!retry)
        {
            foreach (TrainControlRevisionData control in controlRevisions)
                if (NetworkedTrainCar.TryGet(control.CarNetId, out NetworkedTrainCar car))
                    car.Client_SetControlRevision(control.Revision);
            foreach (TrainControlStateData control in controlStates)
                if (NetworkedTrainCar.TryGet(control.CarNetId, out NetworkedTrainCar car))
                    car.Client_ReceiveControlState(control);
        }

        if (!deferred)
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
        return !deferred;
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
            lastApplied.BrakeLines ?? Array.Empty<BrakeLineStateData>(), out failure,
            deferDistantCouplings: BaselineApplied);
    }

    /// <summary>What the host's last applied topology has at one end of a car, for logs.</summary>
    internal string DescribeEndpoint(ushort carNetId, TrainEnd end)
    {
        TrainEndpointStateData endpoint = lastApplied?.Endpoints?.FirstOrDefault(e =>
            e != null && e.CarNetId == carNetId && e.End == end);
        if (endpoint == null)
            return "unknown (no topology for this car yet)";
        return endpoint.HasMechanicalPartner
            ? $"coupled to {endpoint.MechanicalPartnerCarNetId} ({endpoint.MechanicalPartnerEnd})"
            : "uncoupled";
    }

    internal bool TryApplyPending(out string failure)
    {
        failure = string.Empty;
        return pending == null || Receive(pending, out failure);
    }
}
