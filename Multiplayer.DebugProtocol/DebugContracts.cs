using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Multiplayer.Debugging.Protocol;

public enum DebugTraceMode { Summary, Decoded, Raw }
public enum DebugSeverity { Trace, Info, Warning, Error }
public enum DebugRuntimeSide { Shared, Client, Server }

public sealed class DebugSessionInfo
{
    public const int CurrentSchemaVersion = 2;
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
    public string BuildNumber { get; set; } = string.Empty;
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

/// <summary>Filter for the Debug-only <c>POST /api/events/query</c> endpoint.</summary>
public sealed class DebugEventQueryDto
{
    public DateTime? SinceUtc { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DebugSeverity? MinimumSeverity { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed class DebugRuntimeSettingsDto
{
    public DebugTraceMode TraceMode { get; set; } = DebugTraceMode.Summary;
    public bool RawPacketCapture { get; set; }
    public int HighFrequencySampling { get; set; } = 10;
    public string[] Categories { get; set; } = Array.Empty<string>();
    public string[] TracedPacketTypes { get; set; } = Array.Empty<string>();
    public string[] TracedEntities { get; set; } = Array.Empty<string>();
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
