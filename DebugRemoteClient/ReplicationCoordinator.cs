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
            AppliedRecipientCount = value.Recipients.Count(recipient => recipient.Applied),
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
        EvaluateStateComparisons(operation);
        if (operation.StateComparisons.Any(comparison => comparison.Differences.Count > 0) &&
            operation.Status == ReplicationOperationStatus.Complete)
        {
            ItemReplicaComparisonDto comparison = operation.StateComparisons.First(value => value.Differences.Count > 0);
            operation.Status = ReplicationOperationStatus.Discontinuity;
            operation.DiscontinuityReason = $"state:{SessionName(comparison)}:{comparison.Differences[0].Code}";
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

    private static void EvaluateStateComparisons(ReplicationOperationDto operation)
    {
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
            RendererEnabledCount = NullableInt(source, "rendererEnabledCount")
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
        RendererEnabledCount = NullableInt(data, "rendererEnabledCount")
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
