using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Replication;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.DebugClient;

internal sealed class ReplicationCoordinator
{
    private readonly object gate = new();
    private readonly LinkedList<ReplicationOperationDto> operations = new();
    private readonly Dictionary<string, DebugSessionInfo> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingSnapshotStart> pendingSnapshotStarts = new(StringComparer.Ordinal);
    private const int MaximumOperations = 2000;
    private const double PendingSnapshotStartSeconds = 5;

    private sealed class PendingSnapshotStart
    {
        public DebugSessionInfo Session { get; set; }
        public DebugEvent Event { get; set; }
    }

    public event Action<ReplicationOperationDto> DiscontinuityDetected;

    public static void RunSelfTest()
    {
        ReplicationCoordinator coordinator = new();
        DateTime start = DateTime.UtcNow;
        DebugSessionInfo host = new() { SessionId = "host", Role = "host", PlayerId = 0 };
        DebugSessionInfo client = new() { SessionId = "client", Role = "client", PlayerId = 1 };
        coordinator.UpdateSessions(new[] { host, client });
        coordinator.Observe(host, Event(host, 1, start, "item.snapshot-created", "390", "ABC"));
        DebugEvent expected = Event(host, 2, start.AddMilliseconds(5), "item.delivery-expected", "390", "ABC");
        expected.Data["recipientPlayerId"] = 1; expected.Data["recipientPlayerName"] = "Client"; expected.Data["recipientPeerId"] = 4;
        coordinator.Observe(host, expected);
        coordinator.Observe(client, Event(client, 1, start.AddMilliseconds(10), "item.snapshot-received", "390", "ABC"));
        coordinator.Observe(client, Event(client, 2, start.AddMilliseconds(15), "item.snapshot-apply.after", "390", ""));
        ReplicationOperationDto operation = coordinator.SnapshotOperations().Single();
        if (operation.Status != ReplicationOperationStatus.Complete || operation.Recipients.Count != 1 || !operation.Recipients[0].Applied)
            throw new InvalidOperationException("Replication coordinator self-test failed to complete a host/client item flow.");

        ReplicationCoordinator throwAcknowledgementCoordinator = new();
        throwAcknowledgementCoordinator.UpdateSessions(new[] { host, client });
        DebugEvent throwSource = Event(client, 3, start.AddMilliseconds(20),
            "item.snapshot-created", "391", "THROW-R1");
        throwSource.Data["itemState"] = "Thrown";
        throwSource.Data["originatingPlayerId"] = 1;
        throwAcknowledgementCoordinator.Observe(client, throwSource);
        DebugEvent throwExpected = Event(host, 4, start.AddMilliseconds(25),
            "item.delivery-expected", "391", "THROW-R2");
        throwExpected.Data["recipientPlayerId"] = 1;
        throwAcknowledgementCoordinator.Observe(host, throwExpected);
        throwAcknowledgementCoordinator.Observe(client, Event(client, 4,
            start.AddMilliseconds(30), "item.snapshot-received", "391", "THROW-R2"));
        throwAcknowledgementCoordinator.Observe(client, Event(client, 5,
            start.AddMilliseconds(31), "item.local-throw-acknowledged", "391", ""));
        ReplicationOperationDto throwAcknowledgementOperation =
            throwAcknowledgementCoordinator.SnapshotOperations().Single();
        if (throwAcknowledgementOperation.Status != ReplicationOperationStatus.Complete ||
            throwAcknowledgementOperation.Recipients[0].Applied ||
            !throwAcknowledgementOperation.Recipients[0].AcknowledgedWithoutApply)
            throw new InvalidOperationException("Replication coordinator self-test did not complete an originating throw acknowledgement.");

        ReplicationCoordinator twoPlayerCoordinator = new();
        twoPlayerCoordinator.UpdateSessions(new[] { host, client });
        twoPlayerCoordinator.Observe(client, Event(client, 10, start.AddSeconds(1), "item.packet-send-requested", "344", "DROP"));
        twoPlayerCoordinator.Observe(host, Event(host, 10, start.AddSeconds(1).AddMilliseconds(5), "item.snapshot-received", "344", "DROP"));
        twoPlayerCoordinator.Observe(host, Event(host, 11, start.AddSeconds(1).AddMilliseconds(10), "item.snapshot-apply.after", "344", "DROP"));
        twoPlayerCoordinator.Observe(host, Event(host, 12, start.AddSeconds(1).AddMilliseconds(15), "item.relay-requested", "344", "DROP"));
        ReplicationOperationDto zeroRecipientOperation = twoPlayerCoordinator.SnapshotOperations().Single();
        if (zeroRecipientOperation.Status != ReplicationOperationStatus.Complete || zeroRecipientOperation.Recipients.Count != 0)
            throw new InvalidOperationException("Replication coordinator self-test failed to complete an applied host operation with no relay recipients.");

        ReplicationCoordinator projectedOnlyCoordinator = new();
        projectedOnlyCoordinator.UpdateSessions(new[] { host, client });
        projectedOnlyCoordinator.Observe(host,
            Event(host, 16, start.AddSeconds(1.2), "item.snapshot-created", "267", "PROJECTED"));
        if (projectedOnlyCoordinator.SnapshotOperations().Length != 0)
            throw new InvalidOperationException("Replication coordinator exposed a projected snapshot which was never sent.");
        DebugEvent projectedDelivery = Event(host, 17, start.AddSeconds(1.2).AddMilliseconds(5),
            "item.delivery-expected", "267", "PROJECTED");
        projectedDelivery.Data["recipientPlayerId"] = 1;
        projectedOnlyCoordinator.Observe(host, projectedDelivery);
        ReplicationOperationDto projectedDeliveryOperation =
            projectedOnlyCoordinator.SnapshotOperations().Single();
        if (projectedDeliveryOperation.Stages.Count != 2 ||
            projectedDeliveryOperation.Stages[0].EventName != "item.snapshot-created")
            throw new InvalidOperationException("Replication coordinator did not retain the source snapshot for a real delivery.");

        ReplicationCoordinator canonicalFingerprintCoordinator = new();
        canonicalFingerprintCoordinator.UpdateSessions(new[] { host, client });
        canonicalFingerprintCoordinator.Observe(client,
            Event(client, 13, start.AddSeconds(1.1), "item.packet-send-requested", "787", "CLIENT-R1"));
        canonicalFingerprintCoordinator.Observe(host,
            Event(host, 13, start.AddSeconds(1.1).AddMilliseconds(5), "item.snapshot-received", "787", "HOST-R2"));
        canonicalFingerprintCoordinator.Observe(host,
            Event(host, 14, start.AddSeconds(1.1).AddMilliseconds(10), "item.snapshot-apply.after", "787", "HOST-R2"));
        canonicalFingerprintCoordinator.Observe(host,
            Event(host, 15, start.AddSeconds(1.1).AddMilliseconds(15), "item.relay-requested", "787", "HOST-R2"));
        ReplicationOperationDto canonicalFingerprintOperation =
            canonicalFingerprintCoordinator.SnapshotOperations().Single();
        if (canonicalFingerprintOperation.Status != ReplicationOperationStatus.Complete ||
            canonicalFingerprintOperation.Stages.Count != 4)
            throw new InvalidOperationException("Replication coordinator self-test split a canonical host fingerprint from its client request.");

        ReplicationCoordinator observationCoordinator = new();
        observationCoordinator.UpdateSessions(new[] { client });
        observationCoordinator.Observe(client, Event(client, 20, start.AddSeconds(2), "item.local-state-observed", "789", ""));
        observationCoordinator.Observe(client, Event(client, 21, start.AddSeconds(2), "item.local-state-unchanged", "789", ""));
        observationCoordinator.Observe(client, Event(client, 22, start.AddSeconds(2), "item.snapshot-suppressed", "789", ""));
        if (observationCoordinator.SnapshotOperations().Length != 0)
            throw new InvalidOperationException("Replication coordinator self-test created an operation from observational item events.");

        ReplicationCoordinator stateCoordinator = new();
        stateCoordinator.UpdateSessions(new[] { host, client });
        DebugEvent stateSource = Event(host, 30, start.AddSeconds(3), "item.snapshot-created", "737", "STATE");
        stateSource.Data["itemState"] = "Dropped";
        stateSource.Data["authorityRevision"] = 4;
        stateSource.Data["persistentOwnerPlayerId"] = 1;
        stateSource.Data["position"] = new Dictionary<string, object> { ["x"] = 100f, ["y"] = 20f, ["z"] = 300f };
        stateSource.Data["sourceUnityState"] = new Dictionary<string, object>
        {
            ["rendererCount"] = 67, ["rendererEnabledCount"] = 64,
            ["activeInHierarchy"] = true, ["layerName"] = "World_Item", ["rigidbodyIsKinematic"] = false
        };
        stateCoordinator.Observe(host, stateSource);
        DebugEvent stateExpected = Event(host, 31, start.AddSeconds(3).AddMilliseconds(5), "item.delivery-expected", "737", "STATE");
        stateExpected.Data["recipientPlayerId"] = 1;
        stateCoordinator.Observe(host, stateExpected);
        stateCoordinator.Observe(client, Event(client, 30, start.AddSeconds(3).AddMilliseconds(10), "item.snapshot-received", "737", "STATE"));
        DebugEvent badApply = Event(client, 31, start.AddSeconds(3).AddMilliseconds(15), "item.snapshot-apply.after", "737", "STATE");
        badApply.Data["lastState"] = "Dropped"; badApply.Data["authorityRevision"] = 4; badApply.Data["persistentOwnerPlayerId"] = 1;
        badApply.Data["positionAbsolute"] = new Dictionary<string, object> { ["x"] = 100f, ["y"] = 20f, ["z"] = 300f };
        badApply.Data["rendererCount"] = 67; badApply.Data["rendererEnabledCount"] = 67;
        badApply.Data["activeInHierarchy"] = true; badApply.Data["layerName"] = "World_Item"; badApply.Data["rigidbodyIsKinematic"] = false;
        stateCoordinator.Observe(client, badApply);
        ReplicationOperationDto stateOperation = stateCoordinator.SnapshotOperations().Single();
        if (stateOperation.Status != ReplicationOperationStatus.Discontinuity ||
            stateOperation.StateComparisons.Single().Differences.All(value => value.Code != "renderer-state-mismatch"))
            throw new InvalidOperationException("Replication coordinator self-test did not detect a semantic renderer discontinuity.");

        ReplicationCoordinator delayedStateCoordinator = new();
        delayedStateCoordinator.UpdateSessions(new[] { host, client });
        DebugEvent handSource = Event(host, 40, start.AddSeconds(4), "item.snapshot-created", "736", "HAND");
        handSource.Data["itemState"] = "InHand";
        handSource.Data["playerId"] = 0;
        handSource.Data["authorityRevision"] = 3;
        handSource.Data["sourceUnityState"] = new Dictionary<string, object>();
        delayedStateCoordinator.Observe(host, handSource);
        DebugEvent handExpected = Event(host, 41, start.AddSeconds(4).AddMilliseconds(5), "item.delivery-expected", "736", "HAND");
        handExpected.Data["recipientPlayerId"] = 1;
        delayedStateCoordinator.Observe(host, handExpected);
        delayedStateCoordinator.Observe(client, Event(client, 40, start.AddSeconds(4).AddMilliseconds(10), "item.snapshot-received", "736", "HAND"));
        DebugEvent handApplied = Event(client, 41, start.AddSeconds(4).AddMilliseconds(15), "item.snapshot-apply.after", "736", "HAND");
        handApplied.Data["lastState"] = "InHand";
        handApplied.Data["authorityRevision"] = 3;
        delayedStateCoordinator.Observe(client, handApplied);
        DebugEvent delayedMismatch = Event(client, 42, start.AddSeconds(5).AddMilliseconds(500), "desync.item-detected", "736", "");
        delayedMismatch.Category = "desync";
        delayedMismatch.Data["reason"] = "state-mismatch";
        delayedMismatch.Data["expectedState"] = "InHand";
        delayedMismatch.Data["actualState"] = "Dropped";
        delayedMismatch.Data["expectedAuthorityRevision"] = 3;
        delayedStateCoordinator.Observe(client, delayedMismatch);
        ReplicationOperationDto delayedStateOperation = delayedStateCoordinator.SnapshotOperations().Single();
        if (delayedStateOperation.Status != ReplicationOperationStatus.Discontinuity ||
            delayedStateOperation.StateComparisons.Single().Differences.All(value => value.Code != "state-mismatch"))
            throw new InvalidOperationException("Replication coordinator self-test did not attach a delayed replica state mismatch to its authoritative operation.");
    }

    private static DebugEvent Event(DebugSessionInfo session, long sequence, DateTime timestamp, string name, string entityId, string fingerprint) => new()
    {
        Sequence = sequence, SourceSequence = sequence, EventKey = $"{session.SessionId}:{sequence}", TimestampUtc = timestamp,
        SessionId = session.SessionId, Role = session.Role, LocalPlayerId = session.PlayerId, RuntimeSide = session.Role == "host" ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
        Category = "item", EventName = name, EntityType = "Item", EntityId = entityId,
        Data = new Dictionary<string, object> { ["stateFingerprint"] = fingerprint, ["updateType"] = "FullSync" }
    };

    public void UpdateSessions(IEnumerable<DebugSessionInfo> values)
    {
        lock (gate)
        {
            foreach (DebugSessionInfo session in values ?? Array.Empty<DebugSessionInfo>())
                if (session != null && !string.IsNullOrEmpty(session.SessionId)) sessions[session.SessionId] = session;
        }
    }

    public void Observe(DebugSessionInfo session, DebugEvent item)
    {
        // Entity lifecycle, inspector and local-state observation events are useful in the
        // event stream but are not replication attempts. Creating operations for them turns
        // routine unchanged/suppressed observations into permanently-pending flow sludge.
        if (item == null || !IsReplicationFlowEvent(item)) return;
        string itemId = item.EntityType == "Item" ? item.EntityId : Text(item.Data, "itemNetId");
        if (string.IsNullOrWhiteSpace(itemId)) return;
        string fingerprint = Text(item.Data, "stateFingerprint");
        DateTime now = item.TimestampUtc == default ? DateTime.UtcNow : item.TimestampUtc;
        ReplicationOperationDto operation;
        ReplicationOperationDto triggered = null;
        lock (gate)
        {
            if (session != null) sessions[session.SessionId] = session;
            PrunePendingSnapshotStarts(now);
            operation = Match(itemId, fingerprint, item, now);
            if (item.EventName == "item.snapshot-created" && operation == null)
            {
                // CreateUpdateData is also used to project host state while evaluating
                // interest and dirty items. A projection is not a replication operation
                // until a send/delivery boundary follows. Retain it briefly so a real flow
                // still includes the source Unity state used by semantic comparisons.
                pendingSnapshotStarts[item.EventKey] = new PendingSnapshotStart
                {
                    Session = session,
                    Event = item
                };
                if (pendingSnapshotStarts.Count > MaximumOperations)
                {
                    string oldest = pendingSnapshotStarts
                        .OrderBy(value => value.Value.Event.TimestampUtc)
                        .Select(value => value.Key)
                        .First();
                    pendingSnapshotStarts.Remove(oldest);
                }
                return;
            }
            if (operation == null)
            {
                if (TryTakePendingSnapshotStart(itemId, fingerprint, item, now,
                        out DebugSessionInfo pendingSession, out DebugEvent pendingEvent))
                {
                    operation = NewOperation(itemId, Text(pendingEvent.Data, "stateFingerprint"),
                        pendingEvent, pendingSession, pendingEvent.TimestampUtc);
                    AddStage(operation, pendingEvent);
                    ApplySemantics(operation, pendingSession, pendingEvent);
                }
                else
                {
                    operation = NewOperation(itemId, fingerprint, item, session, now);
                }
                operations.AddLast(operation);
                while (operations.Count > MaximumOperations) operations.RemoveFirst();
            }
            AddStage(operation, item);
            ApplySemantics(operation, session, item);
            if (Recalculate(operation, now)) triggered = Snapshot(operation);
        }
        if (triggered != null) DiscontinuityDetected?.Invoke(triggered);
    }

    private void PrunePendingSnapshotStarts(DateTime now)
    {
        foreach (string key in pendingSnapshotStarts
                     .Where(value => (now - value.Value.Event.TimestampUtc).TotalSeconds >
                         PendingSnapshotStartSeconds)
                     .Select(value => value.Key)
                     .ToArray())
            pendingSnapshotStarts.Remove(key);
    }

    private bool TryTakePendingSnapshotStart(string itemId, string fingerprint, DebugEvent current,
        DateTime now, out DebugSessionInfo session, out DebugEvent item)
    {
        KeyValuePair<string, PendingSnapshotStart>? match = pendingSnapshotStarts
            .Where(value => value.Value.Event.EntityId == itemId &&
                value.Value.Event.SessionId == current.SessionId &&
                value.Value.Event.TimestampUtc <= now &&
                (now - value.Value.Event.TimestampUtc).TotalSeconds <= PendingSnapshotStartSeconds &&
                (string.IsNullOrEmpty(fingerprint) || string.Equals(
                    Text(value.Value.Event.Data, "stateFingerprint"), fingerprint,
                    StringComparison.Ordinal)))
            .OrderByDescending(value => value.Value.Event.TimestampUtc)
            .Cast<KeyValuePair<string, PendingSnapshotStart>?>()
            .FirstOrDefault();
        if (!match.HasValue)
        {
            session = null;
            item = null;
            return false;
        }

        pendingSnapshotStarts.Remove(match.Value.Key);
        session = match.Value.Value.Session;
        item = match.Value.Value.Event;
        return true;
    }

    public void Tick(DateTime now)
    {
        List<ReplicationOperationDto> triggered = new();
        lock (gate)
            foreach (ReplicationOperationDto operation in operations.Where(value => value.Status == ReplicationOperationStatus.Pending))
                if (Recalculate(operation, now)) triggered.Add(Snapshot(operation));
        foreach (ReplicationOperationDto operation in triggered) DiscontinuityDetected?.Invoke(operation);
    }

    public ReplicationOperationDto[] SnapshotOperations()
    {
        lock (gate) return operations.Reverse().Select(Snapshot).ToArray();
    }

    public ReplicationOperationSummaryDto[] SnapshotSummaries()
    {
        lock (gate) return operations.Reverse().Take(500).Select(value => new ReplicationOperationSummaryDto
        {
            OperationId = value.OperationId, EntityId = value.EntityId, UpdateType = value.UpdateType,
            OriginSessionId = value.OriginSessionId, OriginPlayerId = value.OriginPlayerId,
            StartedUtc = value.StartedUtc, UpdatedUtc = value.UpdatedUtc, Status = value.Status,
            CorrelationConfidence = value.CorrelationConfidence, DiscontinuityReason = value.DiscontinuityReason,
            StageCount = value.Stages.Count, RecipientCount = value.Recipients.Count,
            AppliedRecipientCount = value.Recipients.Count(recipient => recipient.Applied),
            AcknowledgedRecipientCount = value.Recipients.Count(recipient => recipient.AcknowledgedWithoutApply),
            StateDiscontinuityCount = value.StateComparisons.Sum(comparison => comparison.Differences.Count)
        }).ToArray();
    }

    public ReplicationOperationDto Get(string id)
    {
        lock (gate) return Snapshot(operations.FirstOrDefault(item => string.Equals(item.OperationId, id, StringComparison.Ordinal)));
    }

    private ReplicationOperationDto Match(string itemId, string fingerprint, DebugEvent item, DateTime now)
    {
        IEnumerable<ReplicationOperationDto> candidates = operations.Reverse().Where(value => value.EntityId == itemId && (now - value.UpdatedUtc).TotalSeconds < 5);
        if (item.EventName is "desync.item-detected" or "desync.item-recovered")
        {
            uint? expectedRevision = UInt(item.Data, "expectedAuthorityRevision");
            ReplicationOperationDto semantic = candidates.FirstOrDefault(value =>
                !expectedRevision.HasValue || value.Stages.Any(stage =>
                    UInt(stage.Data, "authorityRevision") == expectedRevision));
            if (semantic != null)
            {
                if (semantic.CorrelationConfidence == "unmatched")
                    semantic.CorrelationConfidence = "semantic";
                return semantic;
            }
        }
        if (!string.IsNullOrEmpty(fingerprint))
        {
            // A client request and the host-authoritative relay intentionally have different
            // fingerprints when the host increments the revision or normalises authority
            // metadata. Once the canonical snapshot has joined an operation, treat every
            // fingerprint carried by its stages as an alias for that same flow. Otherwise the
            // relay starts a second 0/0 operation and leaves the successfully-applied request
            // permanently pending.
            ReplicationOperationDto exact = candidates.FirstOrDefault(value =>
                value.StateFingerprint == fingerprint || value.Stages.Any(stage =>
                    string.Equals(Text(stage.Data, "stateFingerprint"), fingerprint,
                        StringComparison.Ordinal)));
            if (exact != null) { exact.CorrelationConfidence = "exact"; return exact; }

            if (item.EventName == "item.snapshot-created")
            {
                ReplicationOperationDto refinement = candidates.FirstOrDefault(value =>
                    value.OriginSessionId == item.SessionId && value.OriginTick == item.NetworkTick &&
                    (now - value.StartedUtc).TotalMilliseconds <= 100 &&
                    !value.Stages.Any(stage => stage.EventName is "item.delivery-expected" or "item.packet-send-requested"));
                if (refinement != null)
                {
                    refinement.StateFingerprint = fingerprint;
                    refinement.UpdateType = Text(item.Data, "updateType");
                    refinement.CorrelationConfidence = "exact";
                    return refinement;
                }
            }

            if (IsOperationStart(item.EventName))
                return null;
        }
        ReplicationOperationDto recent = candidates.FirstOrDefault(value =>
            (now - value.UpdatedUtc).TotalMilliseconds <= 750 &&
            value.Status == ReplicationOperationStatus.Pending &&
            (string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(value.StateFingerprint)));
        if (recent != null) { if (recent.CorrelationConfidence == "unmatched") recent.CorrelationConfidence = "likely"; return recent; }
        return IsOperationStart(item.EventName) ? null : candidates.FirstOrDefault(value => (now - value.UpdatedUtc).TotalMilliseconds <= 250);
    }

    private static ReplicationOperationDto NewOperation(string itemId, string fingerprint, DebugEvent item, DebugSessionInfo session, DateTime now)
    {
        string operationId = $"item-{itemId}-{now:HHmmssfff}-{Guid.NewGuid():N}";
        return new ReplicationOperationDto
        {
            OperationId = operationId.Substring(0, Math.Min(46, operationId.Length)),
            EntityId = itemId,
            StateFingerprint = fingerprint,
            UpdateType = Text(item.Data, "updateType"),
            OriginSessionId = item.SessionId,
            OriginPlayerId = item.LocalPlayerId ?? session?.PlayerId,
            OriginTick = item.NetworkTick,
            StartedUtc = now,
            UpdatedUtc = now,
            Status = ReplicationOperationStatus.Pending,
            CorrelationConfidence = string.IsNullOrEmpty(fingerprint) ? "unmatched" : "exact"
        };
    }

    private static void AddStage(ReplicationOperationDto operation, DebugEvent item)
    {
        if (operation.Stages.Any(stage => stage.EventKey == item.EventKey)) return;
        operation.UpdatedUtc = item.TimestampUtc;
        operation.Stages.Add(new ReplicationStageDto
        {
            EventKey = item.EventKey,
            SessionId = item.SessionId,
            SourceSequence = item.SourceSequence,
            TimestampUtc = item.TimestampUtc,
            NetworkTick = item.NetworkTick,
            Role = item.Role,
            PlayerId = item.LocalPlayerId,
            RuntimeSide = item.RuntimeSide,
            EventName = item.EventName,
            Severity = item.Severity,
            Data = item.Data == null ? new() : new Dictionary<string, object>(item.Data, StringComparer.Ordinal)
        });
        if (string.IsNullOrEmpty(operation.UpdateType)) operation.UpdateType = Text(item.Data, "updateType");
        if (string.IsNullOrEmpty(operation.StateFingerprint)) operation.StateFingerprint = Text(item.Data, "stateFingerprint");
        string payload = Text(item.Data, "payloadFingerprint"); if (!string.IsNullOrEmpty(payload)) operation.PayloadFingerprint = payload;
    }

    private void ApplySemantics(ReplicationOperationDto operation, DebugSessionInfo session, DebugEvent item)
    {
        if (item.EventName == "item.delivery-expected")
        {
            byte playerId = Byte(item.Data, "recipientPlayerId");
            ReplicationRecipientDto recipient = operation.Recipients.FirstOrDefault(value => value.PlayerId == playerId);
            if (recipient == null) { recipient = new ReplicationRecipientDto { PlayerId = playerId }; operation.Recipients.Add(recipient); }
            recipient.Expected = true; recipient.Sent = true;
            recipient.PlayerName = Text(item.Data, "recipientPlayerName"); recipient.PeerId = Int(item.Data, "recipientPeerId", -1);
            recipient.Decision = Text(item.Data, "decision"); recipient.Interest = new Dictionary<string, object>(item.Data, StringComparer.Ordinal);
            DebugSessionInfo recipientSession = sessions.Values.FirstOrDefault(value => value.Role == "client" && value.PlayerId == playerId);
            recipient.SessionId = recipientSession?.SessionId ?? string.Empty;
        }
        if (session?.Role == "client" && session.PlayerId.HasValue)
        {
            ReplicationRecipientDto recipient = operation.Recipients.FirstOrDefault(value => value.PlayerId == session.PlayerId.Value);
            if (recipient != null)
            {
                recipient.SessionId = session.SessionId;
                recipient.Received |= item.EventName is "packet.handler.before" or "item.snapshot-received" or
                    "item.special-create-deferred";
                recipient.Handled |= item.EventName is "packet.handler.after" or "item.snapshot-received" or
                    "item.snapshot-apply.before" or "item.snapshot-apply.after" or
                    "item.special-create-deferred" or "item.local-throw-acknowledged";
                if (item.EventName == "item.local-throw-acknowledged")
                {
                    recipient.Received = true;
                    recipient.AcknowledgedWithoutApply = true;
                    recipient.ResultingState = new Dictionary<string, object>(item.Data,
                        StringComparer.Ordinal);
                }
                if (item.EventName == "item.snapshot-apply.after")
                {
                    recipient.Applied = true;
                    recipient.ResultingState = new Dictionary<string, object>(item.Data, StringComparer.Ordinal);
                }
            }
        }
    }

    private static bool Recalculate(ReplicationOperationDto operation, DateTime now)
    {
        ReplicationOperationStatus before = operation.Status;
        RecipientCompletionResult completion = RecipientCompletionEvaluator.Evaluate(new RecipientCompletionInput
        {
            LastUpdatedUtc = operation.UpdatedUtc,
            ValidationRejected = operation.Stages.Any(stage => stage.EventName.Contains("validation-rejected")),
            HostApplied = operation.Stages.Any(stage => stage.RuntimeSide == DebugRuntimeSide.Server &&
                stage.EventName == "item.snapshot-apply.after"),
            RelayFinished = operation.Stages.Any(stage => stage.RuntimeSide == DebugRuntimeSide.Server &&
                stage.EventName == "item.relay-requested"),
            Recipients = operation.Recipients.Select(recipient => new RecipientProgress
            {
                PlayerId = recipient.PlayerId,
                Sent = recipient.Sent,
                Received = recipient.Received,
                Applied = recipient.Applied,
                AcknowledgedWithoutApply = recipient.AcknowledgedWithoutApply
            }).ToArray()
        }, now);

        foreach (ReplicationRecipientDto recipient in operation.Recipients)
        {
            recipient.Discontinuity = completion.RecipientDiscontinuities.TryGetValue(recipient.PlayerId,
                out string reason) ? reason : string.Empty;
        }
        operation.Status = completion.Status switch
        {
            ReplicationCompletionStatus.Complete => ReplicationOperationStatus.Complete,
            ReplicationCompletionStatus.Rejected => ReplicationOperationStatus.Rejected,
            ReplicationCompletionStatus.Discontinuity => ReplicationOperationStatus.Discontinuity,
            _ => ReplicationOperationStatus.Pending
        };
        operation.DiscontinuityReason = completion.Reason;
        EvaluateStateComparisons(operation);
        if (operation.StateComparisons.Any(comparison => comparison.Differences.Count > 0) &&
            operation.Status == ReplicationOperationStatus.Complete)
        {
            ItemReplicaComparisonDto comparison = operation.StateComparisons.First(value => value.Differences.Count > 0);
            operation.Status = ReplicationOperationStatus.Discontinuity;
            operation.DiscontinuityReason = $"state:{SessionName(comparison)}:{comparison.Differences[0].Code}";
        }
        ReplicationStageDto activeSemanticDesync = operation.Stages
            .Where(stage => stage.EventName is "desync.item-detected" or "desync.item-recovered")
            .GroupBy(stage => stage.SessionId)
            .Select(group => group.OrderByDescending(stage => stage.TimestampUtc).First())
            .FirstOrDefault(stage => stage.EventName == "desync.item-detected");
        if (activeSemanticDesync != null)
        {
            string reason = Text(activeSemanticDesync.Data, "reason", "state-mismatch");
            ItemReplicaComparisonDto comparison = operation.StateComparisons.FirstOrDefault(value =>
                value.SessionId == activeSemanticDesync.SessionId);
            if (comparison == null)
            {
                comparison = new ItemReplicaComparisonDto
                {
                    SessionId = activeSemanticDesync.SessionId,
                    Role = activeSemanticDesync.Role,
                    PlayerId = activeSemanticDesync.PlayerId,
                    EventName = activeSemanticDesync.EventName
                };
                operation.StateComparisons.Add(comparison);
            }
            comparison.Differences.Add(new ItemReplicaDifferenceDto
            {
                Code = reason,
                Field = reason == "holder-mismatch" ? "holderPlayerId" : "itemState",
                Expected = reason == "holder-mismatch"
                    ? Text(activeSemanticDesync.Data, "expectedHolder")
                    : Text(activeSemanticDesync.Data, "expectedState"),
                Actual = reason == "holder-mismatch"
                    ? Text(activeSemanticDesync.Data, "actualHolder")
                    : Text(activeSemanticDesync.Data, "actualState"),
                Detail = string.Concat("authorityRevision=",
                    Text(activeSemanticDesync.Data, "expectedAuthorityRevision"))
            });
            operation.Status = ReplicationOperationStatus.Discontinuity;
            operation.DiscontinuityReason = $"state:{SessionName(comparison)}:{reason}";
        }
        bool newlyDetected = operation.Status == ReplicationOperationStatus.Discontinuity && before != ReplicationOperationStatus.Discontinuity && !operation.CaptureTriggered;
        if (newlyDetected) operation.CaptureTriggered = true;
        return newlyDetected;
    }

    private static bool IsReplicationFlowEvent(DebugEvent item)
    {
        if (!(item.EntityType == "Item" || item.Category == "item" && !string.IsNullOrEmpty(item.EntityId)))
            return false;

        return item.EventName is
            "item.snapshot-created" or
            "item.packet-send-requested" or
            "item.bulk-item-send-requested" or
            "item.bulk-send-requested" or
            "item.delivery-expected" or
            "item.validation-accepted" or
            "item.validation-rejected" or
            "item.snapshot-received" or
            "item.local-throw-acknowledged" or
            "item.special-create-deferred" or
            "item.snapshot-apply.before" or
            "item.snapshot-apply.after" or
            "item.snapshot-apply.exception" or
            "item.snapshot-deferred" or
            "item.relay-requested" or
            "item.missing-local-representation" or
            "desync.item-detected" or
            "desync.item-recovered" or
            "packet.handler.before" or
            "packet.handler.after" or
            "packet.handler.exception";
    }
    private static bool IsOperationStart(string name) => name is "item.snapshot-created" or "item.packet-send-requested" or "item.relay-requested" or "item.bulk-item-send-requested";

    private static void EvaluateStateComparisons(ReplicationOperationDto operation)
    {
        // A successful Destroy is represented by destroyApplied/applyResult, not by a live
        // Unity state. Comparing it as Dropped/active guarantees false discontinuities.
        if (string.Equals(operation.UpdateType, "Destroy", StringComparison.OrdinalIgnoreCase))
        {
            operation.StateComparisons.Clear();
            return;
        }
        ReplicationStageDto source = operation.Stages.FirstOrDefault(stage =>
            stage.EventName == "item.snapshot-created" && stage.Data != null && stage.Data.ContainsKey("sourceUnityState"));
        if (source == null)
            return;

        ReplicationStageDto canonical = operation.Stages
            .Where(stage => stage.Data != null && stage.Data.ContainsKey("itemState"))
            .OrderByDescending(stage => UInt(stage.Data, "authorityRevision") ?? 0)
            .ThenByDescending(stage => stage.TimestampUtc)
            .FirstOrDefault() ?? source;
        ItemReplicaState expected = ReadExpectedState(canonical.Data, source.Data);
        operation.StateComparisons.Clear();
        foreach (ReplicationStageDto applied in operation.Stages
                     .Where(stage => stage.EventName == "item.snapshot-apply.after")
                     .GroupBy(stage => stage.SessionId)
                     .Select(group => group.OrderByDescending(stage => stage.TimestampUtc).First()))
        {
            ItemReplicaState actual = ReadAppliedState(applied.Data);
            IReadOnlyList<ItemReplicaDifference> differences = ItemReplicaStateComparer.Compare(expected, actual, applied.PlayerId);
            operation.StateComparisons.Add(new ItemReplicaComparisonDto
            {
                SessionId = applied.SessionId,
                Role = applied.Role,
                PlayerId = applied.PlayerId,
                EventName = applied.EventName,
                Differences = differences.Select(value => new ItemReplicaDifferenceDto
                {
                    Code = value.Code,
                    Field = value.Field,
                    Expected = value.Expected,
                    Actual = value.Actual,
                    Detail = value.Detail
                }).ToList()
            });
        }
    }

    private static ItemReplicaState ReadExpectedState(IDictionary<string, object> wire,
        IDictionary<string, object> sourceCarrier)
    {
        IDictionary<string, object> source = ObjectMap(Value(sourceCarrier, "sourceUnityState"));
        IDictionary<string, object> claim = ObjectMap(Value(wire, "inventoryClaim"));
        return new ItemReplicaState
        {
            ItemState = Text(wire, "itemState"),
            AuthorityRevision = UInt(wire, "authorityRevision"),
            PersistentOwnerPlayerId = NullableByte(wire, "persistentOwnerPlayerId"),
            PlacementPlayerId = NullableByte(wire, "playerId"),
            InventoryClaimPlayerId = NullableByte(wire, "inventoryClaimPlayerId") ?? NullableByte(claim, "playerId") ?? NullableByte(source, "inventoryClaimPlayerId"),
            InventoryClaimSlot = NullableInt(wire, "inventoryClaimSlot") ?? NullableInt(claim, "slot") ?? NullableInt(source, "inventoryClaimSlot"),
            InventoryClaimFlags = Text(wire, "inventoryClaimFlags", Text(claim, "flags", Text(source, "inventoryClaimFlags"))),
            PositionAbsolute = Vector(Value(wire, "position")) ?? Vector(Value(wire, "itemPosition")) ?? Vector(Value(source, "positionAbsolute")),
            Velocity = Vector(Value(source, "velocity")),
            ActiveInHierarchy = NullableBool(source, "activeInHierarchy"),
            Parent = Text(source, "parent"),
            LayerName = Text(source, "layerName"),
            RigidbodyIsKinematic = NullableBool(source, "rigidbodyIsKinematic"),
            ActualRemoteHolderPlayerId = NullableByte(source, "actualRemoteHolderPlayerId"),
            RendererCount = NullableInt(source, "rendererCount"),
            RendererEnabledCount = NullableInt(source, "rendererEnabledCount"),
            PageBookCurrentPage = NullableInt(source, "pageBookCurrentPage"),
            PageBookPageCount = NullableInt(source, "pageBookPageCount"),
            PageBookPagesGenerated = NullableBool(source, "pageBookPagesGenerated")
        };
    }

    private static ItemReplicaState ReadAppliedState(IDictionary<string, object> data) => new()
    {
        ItemState = Text(data, "lastState", Text(data, "computedState")),
        AuthorityRevision = UInt(data, "authorityRevision"),
        PersistentOwnerPlayerId = NullableByte(data, "persistentOwnerPlayerId"),
        PlacementPlayerId = NullableByte(data, "placementPlayerId"),
        InventoryClaimPlayerId = NullableByte(data, "inventoryClaimPlayerId"),
        InventoryClaimSlot = NullableInt(data, "inventoryClaimSlot"),
        InventoryClaimFlags = Text(data, "inventoryClaimFlags"),
        PositionAbsolute = Vector(Value(data, "positionAbsolute")),
        Velocity = Vector(Value(data, "velocity")),
        ActiveInHierarchy = NullableBool(data, "activeInHierarchy"),
        Parent = Text(data, "parent"),
        LayerName = Text(data, "layerName"),
        RigidbodyIsKinematic = NullableBool(data, "rigidbodyIsKinematic"),
        ActualRemoteHolderPlayerId = NullableByte(data, "actualRemoteHolderPlayerId"),
        RendererCount = NullableInt(data, "rendererCount"),
        RendererEnabledCount = NullableInt(data, "rendererEnabledCount"),
        PageBookCurrentPage = NullableInt(data, "pageBookCurrentPage"),
        PageBookPageCount = NullableInt(data, "pageBookPageCount"),
        PageBookPagesGenerated = NullableBool(data, "pageBookPagesGenerated")
    };

    private static string SessionName(ItemReplicaComparisonDto value) =>
        value.PlayerId.HasValue ? $"P{value.PlayerId.Value}" : string.IsNullOrEmpty(value.Role) ? "session" : value.Role;
    private static object Value(IDictionary<string, object> data, string key) =>
        data != null && data.TryGetValue(key, out object value) ? value : null;
    private static IDictionary<string, object> ObjectMap(object value)
    {
        if (value is IDictionary<string, object> map) return map;
        if (value is JObject json) return json.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
        return new Dictionary<string, object>();
    }
    private static ReplicaVector3? Vector(object value)
    {
        IDictionary<string, object> map = ObjectMap(value);
        try
        {
            if (!map.ContainsKey("x") || !map.ContainsKey("y") || !map.ContainsKey("z")) return null;
            return new ReplicaVector3(Convert.ToSingle(map["x"]), Convert.ToSingle(map["y"]), Convert.ToSingle(map["z"]));
        }
        catch { return null; }
    }
    private static uint? UInt(IDictionary<string, object> data, string key) { try { object value = Value(data, key); return value == null ? null : Convert.ToUInt32(value); } catch { return null; } }
    private static byte? NullableByte(IDictionary<string, object> data, string key) { try { object value = Value(data, key); return value == null ? null : Convert.ToByte(value); } catch { return null; } }
    private static int? NullableInt(IDictionary<string, object> data, string key) { try { object value = Value(data, key); return value == null ? null : Convert.ToInt32(value); } catch { return null; } }
    private static bool? NullableBool(IDictionary<string, object> data, string key) { try { object value = Value(data, key); return value == null ? null : Convert.ToBoolean(value); } catch { return null; } }
    private static string Text(IDictionary<string, object> data, string key, string fallback) { string value = Text(data, key); return string.IsNullOrEmpty(value) ? fallback : value; }
    private static string Text(IDictionary<string, object> data, string key) => data != null && data.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) : string.Empty;
    private static int Int(IDictionary<string, object> data, string key, int fallback) { try { return data != null && data.TryGetValue(key, out object value) ? Convert.ToInt32(value) : fallback; } catch { return fallback; } }
    private static byte Byte(IDictionary<string, object> data, string key) { try { return data != null && data.TryGetValue(key, out object value) ? Convert.ToByte(value) : (byte)0; } catch { return 0; } }
    private static ReplicationOperationDto Snapshot(ReplicationOperationDto value) => value == null ? null : JsonConvert.DeserializeObject<ReplicationOperationDto>(DebugJson.Serialize(value), DebugJson.Settings);
}
