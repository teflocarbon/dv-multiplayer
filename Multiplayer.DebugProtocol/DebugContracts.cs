using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Multiplayer.Debugging.Protocol;

public enum DebugTraceMode { Summary, Decoded, Raw }
public enum DebugSeverity { Trace, Info, Warning, Error }
public enum DebugRuntimeSide { Shared, Client, Server, Standalone, Dashboard }
public enum ReplicationOperationStatus { Pending, Complete, Rejected, Discontinuity, Ambiguous }

public sealed class DebugSessionInfo
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string SessionId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime ProcessStartedUtc { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public string Role { get; set; } = "idle";
    public string PlayerName { get; set; } = string.Empty;
    public byte? PlayerId { get; set; }
    public int FirehosePort { get; set; }
    public string FirehoseUrl { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
    public string GameBuild { get; set; } = string.Empty;
    public string ModCommit { get; set; } = "unknown";
}

public sealed class DebugEvent
{
    public int SchemaVersion { get; set; } = DebugSessionInfo.CurrentSchemaVersion;
    public long Sequence { get; set; }
    public long SourceSequence { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public int Frame { get; set; }
    public uint? NetworkTick { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DebugRuntimeSide RuntimeSide { get; set; }
    public byte? LocalPlayerId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public DebugSeverity Severity { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string CausationId { get; set; } = string.Empty;
    public bool HighFrequency { get; set; }
    public Dictionary<string, object> Data { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ReplicationStageDto
{
    public string EventKey { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public long SourceSequence { get; set; }
    public DateTime TimestampUtc { get; set; }
    public uint? NetworkTick { get; set; }
    public string Role { get; set; } = string.Empty;
    public byte? PlayerId { get; set; }
    public DebugRuntimeSide RuntimeSide { get; set; }
    public string EventName { get; set; } = string.Empty;
    public DebugSeverity Severity { get; set; }
    public Dictionary<string, object> Data { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ReplicationRecipientDto
{
    public byte PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int PeerId { get; set; } = -1;
    public string SessionId { get; set; } = string.Empty;
    public bool Expected { get; set; }
    public bool Sent { get; set; }
    public bool Received { get; set; }
    public bool Handled { get; set; }
    public bool Applied { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string Discontinuity { get; set; } = string.Empty;
    public Dictionary<string, object> Interest { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object> ResultingState { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ItemReplicaDifferenceDto
{
    public string Code { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Expected { get; set; } = string.Empty;
    public string Actual { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class ItemReplicaComparisonDto
{
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public byte? PlayerId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public List<ItemReplicaDifferenceDto> Differences { get; set; } = new();
}

public sealed class ReplicationOperationDto
{
    public string OperationId { get; set; } = string.Empty;
    public string EntityType { get; set; } = "Item";
    public string EntityId { get; set; } = string.Empty;
    public string PacketType { get; set; } = "CommonItemUpdatePacket";
    public string StateFingerprint { get; set; } = string.Empty;
    public string PayloadFingerprint { get; set; } = string.Empty;
    public string UpdateType { get; set; } = string.Empty;
    public string OriginSessionId { get; set; } = string.Empty;
    public byte? OriginPlayerId { get; set; }
    public uint? OriginTick { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public ReplicationOperationStatus Status { get; set; }
    public string CorrelationConfidence { get; set; } = "unmatched";
    public string DiscontinuityReason { get; set; } = string.Empty;
    public bool CaptureTriggered { get; set; }
    public List<ReplicationStageDto> Stages { get; set; } = new();
    public List<ReplicationRecipientDto> Recipients { get; set; } = new();
    public List<ItemReplicaComparisonDto> StateComparisons { get; set; } = new();
}

public sealed class ReplicationOperationSummaryDto
{
    public string OperationId { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string UpdateType { get; set; } = string.Empty;
    public string OriginSessionId { get; set; } = string.Empty;
    public byte? OriginPlayerId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public ReplicationOperationStatus Status { get; set; }
    public string CorrelationConfidence { get; set; } = string.Empty;
    public string DiscontinuityReason { get; set; } = string.Empty;
    public int StageCount { get; set; }
    public int RecipientCount { get; set; }
    public int AppliedRecipientCount { get; set; }
    public int StateDiscontinuityCount { get; set; }
}

public sealed class DebugRuntimeSettingsDto
{
    public DebugTraceMode TraceMode { get; set; } = DebugTraceMode.Summary;
    public bool RawPacketCapture { get; set; }
    public int HighFrequencySampling { get; set; } = 10;
    public string[] Categories { get; set; } = Array.Empty<string>();
    public string[] TracedPacketTypes { get; set; } = Array.Empty<string>();
    public string[] TracedEntities { get; set; } = Array.Empty<string>();
    public bool AutomaticReplicationCaptures { get; set; }
}

public sealed class DebugEntityDto
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DebugSeverity Severity { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
    public Dictionary<string, object> LatestState { get; set; } = new(StringComparer.Ordinal);
    public List<DebugEvent> Timeline { get; set; } = new();
}

public static class DebugJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        Converters = { new StringEnumConverter() }
    };

    public static string Serialize(object value, Formatting formatting = Formatting.None) =>
        JsonConvert.SerializeObject(value, formatting, Settings);
}
