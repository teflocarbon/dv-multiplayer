using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;

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
        TestCapturePreRoll();
    }

    private static void TestSerializationAndRedaction()
    {
        var snapshot = DebugValueSnapshotter.SnapshotObject(new { Name = "player", Password = "secret", Token = "token" });
        Require((string)snapshot["Password"] == "<redacted>" && (string)snapshot["Token"] == "<redacted>", "sensitive values were not redacted");
        string json = DebugJson.Serialize(new DebugEvent { SessionId = "test", Category = "item", EventName = "item.test", Data = snapshot });
        JObject parsed = JObject.Parse(json);
        Require((string)parsed["sessionId"] == "test" && (int)parsed["schemaVersion"] == 1, "event JSON contract is invalid");
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
