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
    private const int MaximumOperations = 2000;

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

        ReplicationCoordinator twoPlayerCoordinator = new();
        twoPlayerCoordinator.UpdateSessions(new[] { host, client });
        twoPlayerCoordinator.Observe(client, Event(client, 10, start.AddSeconds(1), "item.packet-send-requested", "344", "DROP"));
        twoPlayerCoordinator.Observe(host, Event(host, 10, start.AddSeconds(1).AddMilliseconds(5), "item.snapshot-received", "344", "DROP"));
        twoPlayerCoordinator.Observe(host, Event(host, 11, start.AddSeconds(1).AddMilliseconds(10), "item.snapshot-apply.after", "344", "DROP"));
        twoPlayerCoordinator.Observe(host, Event(host, 12, start.AddSeconds(1).AddMilliseconds(15), "item.relay-requested", "344", "DROP"));
        ReplicationOperationDto zeroRecipientOperation = twoPlayerCoordinator.SnapshotOperations().Single();
        if (zeroRecipientOperation.Status != ReplicationOperationStatus.Complete || zeroRecipientOperation.Recipients.Count != 0)
            throw new InvalidOperationException("Replication coordinator self-test failed to complete an applied host operation with no relay recipients.");

        ReplicationCoordinator observationCoordinator = new();
        observationCoordinator.UpdateSessions(new[] { client });
        observationCoordinator.Observe(client, Event(client, 20, start.AddSeconds(2), "item.local-state-observed", "789", ""));
        observationCoordinator.Observe(client, Event(client, 21, start.AddSeconds(2), "item.local-state-unchanged", "789", ""));
        observationCoordinator.Observe(client, Event(client, 22, start.AddSeconds(2), "item.snapshot-suppressed", "789", ""));
        if (observationCoordinator.SnapshotOperations().Length != 0)
            throw new InvalidOperationException("Replication coordinator self-test created an operation from observational item events.");
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
            operation = Match(itemId, fingerprint, item, now);
            if (operation == null)
            {
                operation = NewOperation(itemId, fingerprint, item, session, now);
                operations.AddLast(operation);
                while (operations.Count > MaximumOperations) operations.RemoveFirst();
            }
            AddStage(operation, item);
            ApplySemantics(operation, session, item);
            if (Recalculate(operation, now)) triggered = Snapshot(operation);
        }
        if (triggered != null) DiscontinuityDetected?.Invoke(triggered);
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
            AppliedRecipientCount = value.Recipients.Count(recipient => recipient.Applied)
        }).ToArray();
    }

    public ReplicationOperationDto Get(string id)
    {
        lock (gate) return Snapshot(operations.FirstOrDefault(item => string.Equals(item.OperationId, id, StringComparison.Ordinal)));
    }

    private ReplicationOperationDto Match(string itemId, string fingerprint, DebugEvent item, DateTime now)
    {
        IEnumerable<ReplicationOperationDto> candidates = operations.Reverse().Where(value => value.EntityId == itemId && (now - value.UpdatedUtc).TotalSeconds < 5);
        if (!string.IsNullOrEmpty(fingerprint))
        {
            ReplicationOperationDto exact = candidates.FirstOrDefault(value => value.StateFingerprint == fingerprint);
            if (exact != null) { exact.CorrelationConfidence = "exact"; return exact; }
        }
        ReplicationOperationDto recent = candidates.FirstOrDefault(value => (now - value.UpdatedUtc).TotalMilliseconds <= 750 && value.Status == ReplicationOperationStatus.Pending);
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
                recipient.Received |= item.EventName is "packet.handler.before" or "item.snapshot-received";
                recipient.Handled |= item.EventName is "packet.handler.after" or "item.snapshot-received" or "item.snapshot-apply.before" or "item.snapshot-apply.after";
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
                Applied = recipient.Applied
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
            "item.snapshot-apply.before" or
            "item.snapshot-apply.after" or
            "item.snapshot-apply.exception" or
            "item.snapshot-deferred" or
            "item.relay-requested" or
            "item.missing-local-representation" or
            "packet.handler.before" or
            "packet.handler.after" or
            "packet.handler.exception";
    }
    private static bool IsOperationStart(string name) => name is "item.snapshot-created" or "item.packet-send-requested" or "item.relay-requested" or "item.bulk-item-send-requested";
    private static string Text(IDictionary<string, object> data, string key) => data != null && data.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) : string.Empty;
    private static int Int(IDictionary<string, object> data, string key, int fallback) { try { return data != null && data.TryGetValue(key, out object value) ? Convert.ToInt32(value) : fallback; } catch { return fallback; } }
    private static byte Byte(IDictionary<string, object> data, string key) { try { return data != null && data.TryGetValue(key, out object value) ? Convert.ToByte(value) : (byte)0; } catch { return 0; } }
    private static ReplicationOperationDto Snapshot(ReplicationOperationDto value) => value == null ? null : JsonConvert.DeserializeObject<ReplicationOperationDto>(DebugJson.Serialize(value), DebugJson.Settings);
}
