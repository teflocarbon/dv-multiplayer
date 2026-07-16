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
#if DEBUG
    public string TestRunId { get; set; } = string.Empty;
    public string TestCaseId { get; set; } = string.Empty;
    public string TestPhaseId { get; set; } = string.Empty;
    public string TestStepId { get; set; } = string.Empty;
#endif
    public bool HighFrequency { get; set; }
    public Dictionary<string, object> Data { get; set; } = new(StringComparer.Ordinal);
}

#if DEBUG
public enum RuntimeTestCommandStatus { Queued, Running, Passed, Failed, FailedDirty, Cancelled, Unsupported }
public enum RuntimeTestMutationKind { ReadOnly, IsolatedMutation, Destructive }
public enum RuntimeScenarioOrchestrationKind { None, InventoryFixturePair }
public enum RuntimeScenarioFixturePolicy { None, InventoryContainerAndItem }
public enum RuntimeScenarioCleanupPolicy { ScenarioOwned, RetireInventoryFixturesAndPurgeRepresentations }
public enum RuntimeScenarioFixtureOwnership { TargetPlayer, HostPlayer }

public sealed class RuntimeTestCapabilitiesDto
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Available { get; set; }
    public bool MainThreadAgentReady { get; set; }
    public string BuildConfiguration { get; set; } = "Debug";
    public string Role { get; set; } = "idle";
    public byte? PlayerId { get; set; }
    public string Scene { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = Array.Empty<string>();
    public string[] Commands { get; set; } = Array.Empty<string>();
    public RuntimeTestDescriptorDto[] Tests { get; set; } = Array.Empty<RuntimeTestDescriptorDto>();
    public Dictionary<string, object> Anchors { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RuntimeTestDescriptorDto
{
    public string TestId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Fidelity { get; set; } = string.Empty;
    public RuntimeTestMutationKind MutationKind { get; set; }
    public string[] RequiredCapabilities { get; set; } = Array.Empty<string>();
    public int TimeoutMilliseconds { get; set; } = 15000;
    public bool IsScenario { get; set; }
    public RuntimeScenarioOrchestrationKind ScenarioOrchestration { get; set; }
    public RuntimeScenarioFixturePolicy FixturePolicy { get; set; }
    public RuntimeScenarioCleanupPolicy CleanupPolicy { get; set; }
    public RuntimeScenarioFixtureOwnership FixtureContainerOwnership { get; set; }
    public RuntimeScenarioFixtureOwnership FixtureItemOwnership { get; set; }
    public string DefaultContainerPrefabName { get; set; } = string.Empty;
    public string DefaultItemPrefabName { get; set; } = string.Empty;
}

public sealed class RuntimeTestCommandDto
{
    public int SchemaVersion { get; set; } = RuntimeTestCapabilitiesDto.CurrentSchemaVersion;
    public string RequestId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string CaseId { get; set; } = string.Empty;
    public string PhaseId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string TargetSessionId { get; set; } = string.Empty;
    public string TargetRole { get; set; } = string.Empty;
    public byte? TargetPlayerId { get; set; }
    public RuntimeTestMutationKind MutationKind { get; set; }
    public int TimeoutMilliseconds { get; set; } = 15000;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RuntimeTestCommandAcceptedDto
{
    public string RequestId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string StatusUrl { get; set; } = string.Empty;
}

public sealed class RuntimeTestRunDto
{
    public string RequestId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string CaseId { get; set; } = string.Empty;
    public string PhaseId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public RuntimeTestCommandStatus Status { get; set; }
    public DateTime QueuedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Role { get; set; } = string.Empty;
    public byte? PlayerId { get; set; }
    public string Error { get; set; } = string.Empty;
    public Dictionary<string, object> Result { get; set; } = new(StringComparer.Ordinal);
    public List<RuntimeTestProcessRunDto> Processes { get; set; } = new();
}

public sealed class RuntimeTestRunSummaryDto
{
    public string RequestId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string CaseId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public RuntimeTestCommandStatus Status { get; set; }
    public DateTime QueuedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string PhaseId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ProcessCount { get; set; }
    public bool? CleanupClean { get; set; }
    public string[] CaptureFiles { get; set; } = Array.Empty<string>();
}

public sealed class RuntimeTestProcessRunDto
{
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public byte? PlayerId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string PhaseId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public RuntimeTestCommandStatus Status { get; set; }
    public string Error { get; set; } = string.Empty;
    public Dictionary<string, object> Result { get; set; } = new(StringComparer.Ordinal);
}

public enum RuntimeEnvironmentStage
{
    Idle, LaunchingHost, WaitingForHostAgent, LoadingHostSave, WaitingForHostServer,
    LaunchingClient, WaitingForClientAgent, ConnectingClient, WaitingForClientWorld,
    Ready, Stopping, Stopped, Failed
}

public sealed class RuntimeEnvironmentStartRequestDto
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string CommonArguments { get; set; } = string.Empty;
    public string HostArguments { get; set; } = string.Empty;
    public string ClientArguments { get; set; } = string.Empty;
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7777;
    public string Password { get; set; } = string.Empty;
    public string ServerName { get; set; } = "DVMP automated test";
    public int MaxPlayers { get; set; } = 2;
    public bool MinimizeManagedWindows { get; set; }
    public bool DisableManagedWindowInput { get; set; } = true;
    public int AgentTimeoutSeconds { get; set; } = 120;
    public int WorldTimeoutSeconds { get; set; } = 300;
}

public sealed class RuntimeEnvironmentConfigurationDto
{
    public bool Exists { get; set; }
    public RuntimeEnvironmentStartRequestDto Configuration { get; set; } = new();
}

public sealed class DashboardAutomationInfoDto
{
    public int ApiVersion { get; set; } = 1;
    public string Name { get; set; } = "DVMP Runtime Harness";
    public string BuildNumber { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public bool RuntimeTestsAvailable { get; set; }
    public bool RuntimeEnvironmentAvailable { get; set; }
    public string[] Endpoints { get; set; } = Array.Empty<string>();
}

public sealed class DebugEventQueryDto
{
    public DateTime? SinceUtc { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string TestRunId { get; set; } = string.Empty;
    public string TestCaseId { get; set; } = string.Empty;
    public string TestPhaseId { get; set; } = string.Empty;
    public string TestStepId { get; set; } = string.Empty;
    public DebugSeverity? MinimumSeverity { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed class RuntimeEnvironmentStatusDto
{
    public RuntimeEnvironmentStage Stage { get; set; }
    public bool Active { get; set; }
    public bool Ready { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public int? HostProcessId { get; set; }
    public int? ClientProcessId { get; set; }
    public string HostSessionId { get; set; } = string.Empty;
    public string ClientSessionId { get; set; } = string.Empty;
    public Dictionary<string, object> HostReadiness { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, object> ClientReadiness { get; set; } = new(StringComparer.Ordinal);
}
#endif

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
    public bool AcknowledgedWithoutApply { get; set; }
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
    public int AcknowledgedRecipientCount { get; set; }
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
