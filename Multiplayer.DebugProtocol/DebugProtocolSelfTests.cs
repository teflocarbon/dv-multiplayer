using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Multiplayer.Debugging.Protocol;

public static class DebugProtocolSelfTests
{
    public static void Run()
    {
        TestSerializationAndRedaction();
        TestBoundedStoreAndPriority();
        TestFingerprint();
        TestStateFingerprintAndSourceIdentity();
        TestDiscoveryAndRandomPort();
        TestServerReleasesSseSockets();
        TestCapturePreRoll();
#if DEBUG
        TestRuntimeScenarioDescriptorSerialization();
        TestRuntimeTestEndpoints();
#endif
    }

    private static void TestServerReleasesSseSockets()
    {
        DebugSessionInfo session = Session(Process.GetCurrentProcess());
        DebugEventStore store = new(10);
        using DebugHttpServer server = new(store, () => session);
        server.Start();
        int port = server.Port;
        using TcpClient sse = new();
        sse.Connect(IPAddress.Loopback, port);
        byte[] request = Encoding.ASCII.GetBytes(
            "GET /events HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: keep-alive\r\n\r\n");
        sse.GetStream().Write(request, 0, request.Length);
        Thread.Sleep(25);
        server.Dispose();
        TcpListener replacement = new(IPAddress.Loopback, port);
        try { replacement.Start(); }
        finally { replacement.Stop(); }
    }

    private static void TestSerializationAndRedaction()
    {
        var snapshot = DebugValueSnapshotter.SnapshotObject(new { Name = "player", Password = "secret", Token = "token" });
        Require((string)snapshot["Password"] == "<redacted>" && (string)snapshot["Token"] == "<redacted>", "sensitive values were not redacted");
        string json = DebugJson.Serialize(new DebugEvent { SessionId = "test", Category = "item", EventName = "item.test", Data = snapshot });
        JObject parsed = JObject.Parse(json);
        Require((string)parsed["sessionId"] == "test" &&
            (int)parsed["schemaVersion"] == DebugSessionInfo.CurrentSchemaVersion,
            "event JSON contract is invalid");
    }

    private static void TestBoundedStoreAndPriority()
    {
        DebugEventStore store = new(100);
        store.Publish(new DebugEvent { EventName = "important", Severity = DebugSeverity.Error });
        for (int index = 0; index < 110; index++) store.Publish(new DebugEvent { EventName = "sample", HighFrequency = true });
        DebugEvent[] events = store.Snapshot();
        Require(events.Length == 100, "bounded event store capacity failed");
        Require(events.Any(item => item.EventName == "important"), "sample eviction discarded an error event");
        Require(events.Select(item => item.Sequence).Distinct().Count() == events.Length, "event sequence is not unique");
    }

    private static void TestFingerprint()
    {
        byte[] bytes = { 1, 2, 3, 4 };
        string first = DebugPayloadFingerprint.Compute(bytes, 0, bytes.Length);
        Require(first == DebugPayloadFingerprint.Compute(bytes, 0, bytes.Length), "fingerprint is not stable");
        bytes[3] = 5;
        Require(first != DebugPayloadFingerprint.Compute(bytes, 0, bytes.Length), "fingerprint did not detect a payload change");
    }

    private static void TestStateFingerprintAndSourceIdentity()
    {
        string first = DebugStateFingerprint.Compute(new { State = "Thrown", Values = new System.Collections.Generic.Dictionary<string, object> { ["b"] = 2, ["a"] = 1 } });
        string second = DebugStateFingerprint.Compute(new { Values = new System.Collections.Generic.Dictionary<string, object> { ["a"] = 1, ["b"] = 2 }, State = "Thrown" });
        Require(first == second, "canonical state fingerprint depends on property ordering");
        DebugEventStore store = new(100);
        DebugEvent item = store.Publish(new DebugEvent { SessionId = "host-session", EventName = "identity" });
        Require(item.SourceSequence == 1 && item.EventKey == "host-session:1", "source event identity was not assigned");
        DebugEvent merged = store.Publish(new DebugEvent { SessionId = "client-session", Sequence = 72, EventName = "merged" });
        Require(merged.SourceSequence == 72 && merged.EventKey == "client-session:72" && merged.Sequence != merged.SourceSequence, "merged event lost source identity");
    }

    private static void TestDiscoveryAndRandomPort()
    {
        string root = TemporaryRoot();
        Process process = Process.GetCurrentProcess();
        DebugSessionInfo session = Session(process);
        try
        {
            using DebugDiscoveryFile discovery = new(root, () => session);
            Require(File.Exists(discovery.Path), "discovery file was not created");
            Require(DebugDiscoveryFile.IsLive(session, DateTime.UtcNow, TimeSpan.FromSeconds(10)), "live discovery session was rejected");
            using DebugHttpServer server = new(new DebugEventStore(100), () => session);
            server.Start();
            Require(server.Port > 0 && server.Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), "random loopback server did not start");
            using WebClient client = new();
            JObject response = JObject.Parse(client.DownloadString(server.Url + "api/session"));
            Require((string)response["sessionId"] == session.SessionId, "HTTP session endpoint failed");
        }
        finally { TryDelete(root); }
    }

    private static void TestCapturePreRoll()
    {
        string root = TemporaryRoot();
        DebugEventStore store = new(100);
        DebugSessionInfo session = Session(Process.GetCurrentProcess());
        try
        {
            store.Publish(new DebugEvent { TimestampUtc = DateTime.UtcNow, SessionId = session.SessionId, EventName = "before" });
            using DebugCaptureManager capture = new(store, () => session, root);
            string path = capture.Start("self-test", "shared-id");
            store.Publish(new DebugEvent { SessionId = session.SessionId, EventName = "after" });
            capture.Stop();
            string content = File.ReadAllText(path);
            Require(content.Contains("\"captureId\":\"shared-id\"") && content.Contains("\"before\"") && content.Contains("\"after\""), "capture pre-roll or live write failed");
        }
        finally { TryDelete(root); }
    }

#if DEBUG
    private static void TestRuntimeScenarioDescriptorSerialization()
    {
        RuntimeTestDescriptorDto descriptor = new()
        {
            TestId = "scenario.self-test",
            IsScenario = true,
            ScenarioOrchestration = RuntimeScenarioOrchestrationKind.InventoryFixturePair,
            FixturePolicy = RuntimeScenarioFixturePolicy.InventoryContainerAndItem,
            CleanupPolicy = RuntimeScenarioCleanupPolicy.RetireInventoryFixturesAndPurgeRepresentations,
            FixtureContainerOwnership = RuntimeScenarioFixtureOwnership.TargetPlayer,
            FixtureItemOwnership = RuntimeScenarioFixtureOwnership.HostPlayer,
            DefaultContainerPrefabName = "ItemContainerCrate",
            DefaultItemPrefabName = "lighter"
        };
        string json = DebugJson.Serialize(descriptor);
        RuntimeTestDescriptorDto restored = JsonConvert.DeserializeObject<RuntimeTestDescriptorDto>(
            json, DebugJson.Settings);
        Require(restored?.IsScenario == true &&
            restored.ScenarioOrchestration == RuntimeScenarioOrchestrationKind.InventoryFixturePair &&
            restored.FixturePolicy == RuntimeScenarioFixturePolicy.InventoryContainerAndItem &&
            restored.CleanupPolicy == RuntimeScenarioCleanupPolicy.RetireInventoryFixturesAndPurgeRepresentations &&
            restored.FixtureContainerOwnership == RuntimeScenarioFixtureOwnership.TargetPlayer &&
            restored.FixtureItemOwnership == RuntimeScenarioFixtureOwnership.HostPlayer &&
            restored.DefaultContainerPrefabName == descriptor.DefaultContainerPrefabName &&
            restored.DefaultItemPrefabName == descriptor.DefaultItemPrefabName,
            "runtime scenario descriptor metadata did not round-trip");
    }

    private static void TestRuntimeTestEndpoints()
    {
        DebugSessionInfo session = Session(Process.GetCurrentProcess());
        session.BuildNumber = "2026.07.16.0001";
        RuntimeTestRunDto status = new()
        {
            RequestId = "request-1", RunId = "run-1", Command = "runtime.self-check",
            Status = RuntimeTestCommandStatus.Queued, QueuedUtc = DateTime.UtcNow
        };
        DebugEventStore serverStore = new(100);
        DebugEvent historicalEvent = new()
        {
            TimestampUtc = DateTime.UtcNow.AddMilliseconds(-1), SessionId = "historical-session",
            SourceSequence = 7, EventKey = "historical-session:7", Category = "packet",
            EventName = "packet.raw.send", EntityType = "Item", EntityId = "43",
            Severity = DebugSeverity.Info, CorrelationId = "query-test", TestRunId = "scenario-run"
        };
        using DebugHttpServer server = new(serverStore, () => session,
            historicalEvents: query => query.TestRunId == "scenario-run"
                ? new[] { historicalEvent }
                : Array.Empty<DebugEvent>());
        server.ConfigureRuntimeTests(
            () => new RuntimeTestCapabilitiesDto { Available = true, MainThreadAgentReady = true, Commands = new[] { "runtime.self-check" } },
            () => new[] { new RuntimeTestRunSummaryDto { RequestId = status.RequestId,
                RunId = status.RunId, Command = status.Command, Status = status.Status,
                QueuedUtc = status.QueuedUtc } },
            command => new RuntimeTestCommandAcceptedDto
            {
                RequestId = command.RequestId, RunId = command.RunId, Accepted = true,
                StatusUrl = "/api/runtime-tests/runs/" + command.RequestId
            },
            requestId => requestId == status.RequestId ? status : null,
            requestId => requestId == status.RequestId);
        RuntimeEnvironmentStatusDto environment = new() { Stage = RuntimeEnvironmentStage.Idle, Message = "idle" };
        RuntimeEnvironmentStartRequestDto savedEnvironment = new() { Address = "127.0.0.1", Port = 7777 };
        serverStore.Publish(new DebugEvent { SessionId = session.SessionId, Category = "runtime-test", EventName = "runtime-test.completed", EntityType = "Item", EntityId = "42", Severity = DebugSeverity.Info, CorrelationId = "query-test", TestRunId = "scenario-run" });
        server.ConfigureRuntimeEnvironment(() => environment, request =>
        {
            environment = new RuntimeEnvironmentStatusDto { Stage = RuntimeEnvironmentStage.LaunchingHost, Active = true, Message = request.ExecutablePath };
            return environment;
        }, () => environment = new RuntimeEnvironmentStatusDto { Stage = RuntimeEnvironmentStage.Stopped, Message = "stopped" },
            () => new RuntimeEnvironmentConfigurationDto { Exists = true, Configuration = savedEnvironment },
            request => savedEnvironment = request);
        server.Start();

        using WebClient unauthorized = new();
        bool forbidden = false;
        try { unauthorized.DownloadString(server.Url + "api/runtime-tests/capabilities"); }
        catch (WebException exception) { forbidden = (exception.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.Forbidden; }
        Require(forbidden, "runtime-test capabilities did not require authentication");

        using WebClient client = new();
        client.Headers["X-DVMP-Debug-Token"] = session.ApiToken;
        string dashboard = client.DownloadString(server.Url);
        Require(dashboard.Contains("/runtime-tests.js") && dashboard.Contains("/runtime-tests.css") &&
            dashboard.Contains("/runtime-environment.js") && dashboard.Contains("/runtime-environment.css"), "debug runtime dashboard assets were not injected");
        Require(client.DownloadString(server.Url + "runtime-tests.js").Contains("/api/runtime-tests/commands"), "debug runtime-test dashboard script was not served");
        Require(client.DownloadString(server.Url + "runtime-environment.js").Contains("/api/runtime-environment/start"), "debug environment dashboard script was not served");
        DashboardAutomationInfoDto automation = JsonConvert.DeserializeObject<DashboardAutomationInfoDto>(
            client.DownloadString(server.Url + "api/automation"), DebugJson.Settings);
        Require(automation?.ApiVersion == 1 && automation.BuildNumber == session.BuildNumber &&
            automation.RuntimeTestsAvailable && automation.RuntimeEnvironmentAvailable,
            "dashboard automation discovery endpoint did not report the runtime build");
        JObject capabilities = JObject.Parse(client.DownloadString(server.Url + "api/runtime-tests/capabilities"));
        Require((bool)capabilities["available"] && (bool)capabilities["mainThreadAgentReady"], "runtime-test capabilities endpoint failed");
        client.Headers[HttpRequestHeader.ContentType] = "application/json";
        string body = DebugJson.Serialize(new RuntimeTestCommandDto
        {
            RequestId = status.RequestId, RunId = status.RunId, Command = status.Command
        });
        RuntimeTestCommandAcceptedDto accepted = JsonConvert.DeserializeObject<RuntimeTestCommandAcceptedDto>(
            client.UploadString(server.Url + "api/runtime-tests/commands", body), DebugJson.Settings);
        Require(accepted?.Accepted == true && accepted.RequestId == status.RequestId, "runtime-test command endpoint failed");
        RuntimeTestRunDto returned = JsonConvert.DeserializeObject<RuntimeTestRunDto>(
            client.DownloadString(server.Url + "api/runtime-tests/runs/" + status.RequestId), DebugJson.Settings);
        Require(returned?.Status == RuntimeTestCommandStatus.Queued, "runtime-test status endpoint failed");
        RuntimeTestRunSummaryDto[] history = JsonConvert.DeserializeObject<RuntimeTestRunSummaryDto[]>(
            client.DownloadString(server.Url + "api/runtime-tests/runs"), DebugJson.Settings);
        Require(history?.Length == 1 && history[0].RequestId == status.RequestId,
            "runtime-test history endpoint failed");
        DebugEvent[] queriedEvents = JsonConvert.DeserializeObject<DebugEvent[]>(client.UploadString(server.Url + "api/events/query",
            DebugJson.Serialize(new DebugEventQueryDto { CorrelationId = "query-test", EntityType = "Item", TestRunId = "scenario-run", Limit = 10 })), DebugJson.Settings);
        Require(queriedEvents?.Length == 2 && queriedEvents.Any(item => item.EntityId == "42") &&
            queriedEvents.Any(item => item.EntityId == "43"),
            "filtered live and historical event query endpoint failed");
        RuntimeEnvironmentStatusDto initialEnvironment = JsonConvert.DeserializeObject<RuntimeEnvironmentStatusDto>(
            client.DownloadString(server.Url + "api/runtime-environment/status"), DebugJson.Settings);
        Require(initialEnvironment?.Stage == RuntimeEnvironmentStage.Idle, "runtime environment status endpoint failed");
        RuntimeEnvironmentConfigurationDto initialConfiguration = JsonConvert.DeserializeObject<RuntimeEnvironmentConfigurationDto>(
            client.DownloadString(server.Url + "api/runtime-environment/config"), DebugJson.Settings);
        Require(initialConfiguration?.Exists == true && initialConfiguration.Configuration?.Port == 7777, "runtime environment configuration GET endpoint failed");
        client.UploadString(server.Url + "api/runtime-environment/config", DebugJson.Serialize(new RuntimeEnvironmentStartRequestDto
        {
            ExecutablePath = "persisted-game.exe", Address = "10.0.0.2", Port = 7788, Password = "test-password"
        }));
        Require(savedEnvironment?.ExecutablePath == "persisted-game.exe" && savedEnvironment.Address == "10.0.0.2" &&
            savedEnvironment.Port == 7788 && savedEnvironment.Password == "test-password", "runtime environment configuration POST endpoint failed");
        RuntimeEnvironmentStatusDto startedEnvironment = JsonConvert.DeserializeObject<RuntimeEnvironmentStatusDto>(
            client.UploadString(server.Url + "api/runtime-environment/start", DebugJson.Serialize(new RuntimeEnvironmentStartRequestDto { ExecutablePath = "test-game.exe" })), DebugJson.Settings);
        Require(startedEnvironment?.Stage == RuntimeEnvironmentStage.LaunchingHost, "runtime environment start endpoint failed");
        RuntimeEnvironmentStatusDto stoppedEnvironment = JsonConvert.DeserializeObject<RuntimeEnvironmentStatusDto>(
            client.UploadString(server.Url + "api/runtime-environment/stop", "{}"), DebugJson.Settings);
        Require(stoppedEnvironment?.Stage == RuntimeEnvironmentStage.Stopped, "runtime environment stop endpoint failed");
    }
#endif

    private static DebugSessionInfo Session(Process process) => new()
    {
        SessionId = "self-test-" + Guid.NewGuid().ToString("N"), ProcessId = process.Id,
        ProcessStartedUtc = process.StartTime.ToUniversalTime(), StartedUtc = DateTime.UtcNow,
        HeartbeatUtc = DateTime.UtcNow, Role = "test", ApiToken = Guid.NewGuid().ToString("N")
    };
    private static string TemporaryRoot() { string path = Path.Combine(Path.GetTempPath(), "dvmp-debug-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("DebugProtocol self-test failed: " + message); }
}
