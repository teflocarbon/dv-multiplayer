using Multiplayer.Core.Trains;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Train;
using Multiplayer.Shared.Trains;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Trains;

internal sealed class TrainTopologyAuthority
{
    private readonly Dictionary<(byte PlayerId, ulong OperationId),
        ClientboundTrainTopologySnapshotPacket> completedOperations = new();
    private readonly Queue<(byte PlayerId, ulong OperationId)> completedOrder = new();
    private readonly Dictionary<byte, ulong> lastOperationByPlayer = new();
    private const int MaxCompletedOperations = 512;

    private uint revision;
    private TrainTopologyState canonical;
    private string fingerprint = string.Empty;

    internal uint Revision => revision;

    /// <summary>Why the last <see cref="Execute"/> refused a command, where the result code doesn't say.</summary>
    internal string LastRejectionDetail { get; private set; }

    internal ClientboundTrainTopologySnapshotPacket Snapshot(uint baselineId = 0,
        ulong operationId = 0, TrainTopologyResultCode result = TrainTopologyResultCode.Accepted)
    {
        RefreshObservedNative();
        return BuildSnapshot(baselineId, operationId, result,
            NetworkedTrainCar.GetAllRegistered().Select(value => value.NetId));
    }

    internal ClientboundTrainTopologySnapshotPacket Execute(byte playerId,
        ServerboundTrainTopologyCommandPacket packet, Func<bool> runtimeAuthorized,
        Func<bool> runtimeInRange)
    {
        LastRejectionDetail = null;
        var key = (playerId, packet.OperationId);
        if (packet.OperationId != 0 && completedOperations.TryGetValue(key, out var completed))
        {
            LastRejectionDetail = "a repeat of an operation already answered";
            return completed;
        }

        RefreshObservedNative();
        TrainTopologyCommand command = packet.ToCore();
        if (packet.OperationId != 0)
        {
            if (lastOperationByPlayer.TryGetValue(playerId, out ulong lastOperation) &&
                packet.OperationId <= lastOperation)
            {
                LastRejectionDetail = $"operation {packet.OperationId & 0x00FFFFFFFFFFFFFFUL} isn't " +
                    $"after this player id's last, {lastOperation & 0x00FFFFFFFFFFFFFFUL}";
                return BuildSnapshot(0, packet.OperationId,
                    TrainTopologyResultCode.StaleRevision, Array.Empty<ushort>());
            }

            lastOperationByPlayer[playerId] = packet.OperationId;
            // Commands are delivered reliably and ordered per player. Unrelated native
            // topology changes can advance the global revision while an interaction is
            // in flight, so validate a new intent against the current canonical graph.
            // Endpoint occupancy and all other safety invariants are still checked below.
            command.ExpectedRevision = revision;
        }
        TrainTopologyResult planned = TrainTopologyStateMachine.Apply(canonical, command);
        ushort[] affectedControlCars =
            command.Kind == TrainTopologyCommandKind.ConnectMultipleUnit ||
            command.Kind == TrainTopologyCommandKind.DisconnectMultipleUnit
                ? planned.AffectedEndpoints.Select(endpoint => endpoint.CarNetId)
                    .Distinct().ToArray()
                : Array.Empty<ushort>();
        TrainTopologyResultCode result = planned.Code;
        if (!runtimeAuthorized())
            result = TrainTopologyResultCode.AuthorityDenied;
        else if (!runtimeInRange())
            result = TrainTopologyResultCode.OutOfRange;

        if (result == TrainTopologyResultCode.Accepted)
        {
            TrainTopologyState before = canonical.Clone();
            BrakeLineStateData[] beforeLines = TrainTopologyNativeAdapter.CaptureBrakeLines();
            bool applied = TrainTopologyNativeAdapter.TryApplyCommand(command, packet.PlayAudio,
                out string nativeFailure);
            TrainTopologyState actual = TrainTopologyNativeAdapter.Capture(revision + 1);
            if (!applied || !TrainTopologyStateMachine.Validate(actual, out _))
            {
                string rollbackFailure;
                using (TrainChangeCauses.Scope(TrainChangeCauses.Cause(UncoupleReason.HostRolledBack,
                           detail: $"{packet.Kind} command couldn't be applied: {nativeFailure}")))
                    TrainTopologyNativeAdapter.TryReconcile(before, beforeLines, out rollbackFailure);
                Multiplayer.LogWarning($"Train topology operation {packet.OperationId} failed: " +
                    $"{nativeFailure}; rollback: {rollbackFailure}");
                LastRejectionDetail = $"the host couldn't apply it ({nativeFailure}) and rolled back";
                canonical = TrainTopologyNativeAdapter.Capture(revision);
                fingerprint = TrainTopologyNativeAdapter.Fingerprint(canonical);
                result = TrainTopologyResultCode.NativeMutationFailed;
            }
            else
            {
                foreach (ushort carNetId in affectedControlCars)
                    if (NetworkedTrainCar.TryGet(carNetId, out NetworkedTrainCar affectedCar))
                        affectedCar.Server().CommitControlRevision();
                revision++;
                canonical = actual;
                fingerprint = TrainTopologyNativeAdapter.Fingerprint(canonical);
            }
        }

        ClientboundTrainTopologySnapshotPacket snapshot =
            BuildSnapshot(0, packet.OperationId, result, affectedControlCars);
        Remember(key, snapshot);
        return snapshot;
    }

    internal ClientboundTrainTopologySnapshotPacket CommitObserved(ulong operationId = 0,
        IEnumerable<ushort> controlCars = null)
    {
        TrainTopologyState actual = TrainTopologyNativeAdapter.Capture(revision);
        string actualFingerprint = TrainTopologyNativeAdapter.Fingerprint(actual);
        var affectedControlCars = new HashSet<ushort>(controlCars ?? Array.Empty<ushort>());
        if (canonical == null)
        {
            revision = 1;
            canonical = actual.Clone(revision);
            fingerprint = TrainTopologyNativeAdapter.Fingerprint(canonical);
        }
        else if (!string.Equals(actualFingerprint, fingerprint, StringComparison.Ordinal))
        {
            foreach (TrainEndpointState endpoint in actual.Endpoints)
            {
                canonical.TryGet(endpoint.Endpoint, out TrainEndpointState previous);
                if (previous?.MultipleUnitPartner == endpoint.MultipleUnitPartner)
                    continue;
                affectedControlCars.Add(endpoint.Endpoint.CarNetId);
                if (previous?.MultipleUnitPartner is TrainEndpointId oldPartner)
                    affectedControlCars.Add(oldPartner.CarNetId);
                if (endpoint.MultipleUnitPartner is TrainEndpointId newPartner)
                    affectedControlCars.Add(newPartner.CarNetId);
            }
            foreach (ushort carNetId in affectedControlCars)
                if (NetworkedTrainCar.TryGet(carNetId, out NetworkedTrainCar car))
                    car.Server().CommitControlRevision();
            if (revision != uint.MaxValue)
                revision++;
            canonical = actual.Clone(revision);
            fingerprint = TrainTopologyNativeAdapter.Fingerprint(canonical);
        }
        return BuildSnapshot(0, operationId, TrainTopologyResultCode.Accepted,
            affectedControlCars);
    }

    private void RefreshObservedNative()
    {
        TrainTopologyState actual = TrainTopologyNativeAdapter.Capture(revision);
        string actualFingerprint = TrainTopologyNativeAdapter.Fingerprint(actual);
        if (canonical == null)
        {
            revision = 1;
            canonical = actual.Clone(revision);
            fingerprint = TrainTopologyNativeAdapter.Fingerprint(canonical);
            return;
        }
        if (string.Equals(actualFingerprint, fingerprint, StringComparison.Ordinal))
            return;
        if (revision != uint.MaxValue)
            revision++;
        canonical = actual.Clone(revision);
        fingerprint = TrainTopologyNativeAdapter.Fingerprint(canonical);
    }

    private ClientboundTrainTopologySnapshotPacket BuildSnapshot(uint baselineId,
        ulong operationId, TrainTopologyResultCode result, IEnumerable<ushort> controlCars) => new()
    {
        BaselineId = baselineId,
        TopologyRevision = revision,
        OperationId = operationId,
        Result = result,
        Endpoints = canonical?.Endpoints.OrderBy(value => value.Endpoint)
            .Select(TrainEndpointStateData.FromCore).ToArray() ?? Array.Empty<TrainEndpointStateData>(),
        BrakeLines = TrainTopologyNativeAdapter.CaptureBrakeLines(),
        ControlRevisions = NetworkedTrainCar.GetAllRegistered()
            .OrderBy(value => value.NetId)
            .Select(value => new TrainControlRevisionData
            {
                CarNetId = value.NetId,
                Revision = value.ControlRevision
            }).ToArray(),
        ControlStates = (controlCars ?? Array.Empty<ushort>()).Where(value => value != 0)
            .Distinct().Select(value => NetworkedTrainCar.TryGet(value, out NetworkedTrainCar car)
                ? car.CaptureControlState() : null).Where(value => value != null).ToArray()
    };

    private void Remember((byte PlayerId, ulong OperationId) key,
        ClientboundTrainTopologySnapshotPacket snapshot)
    {
        if (key.OperationId == 0)
            return;
        completedOperations[key] = snapshot;
        completedOrder.Enqueue(key);
        while (completedOrder.Count > MaxCompletedOperations)
            completedOperations.Remove(completedOrder.Dequeue());
    }
}
