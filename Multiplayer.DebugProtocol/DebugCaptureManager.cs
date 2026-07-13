using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Multiplayer.Debugging.Protocol;

public sealed class DebugCaptureManager : IDisposable
{
    private readonly object gate = new();
    private readonly DebugEventStore store;
    private readonly Func<DebugSessionInfo> session;
    private readonly string captureRoot;
    private StreamWriter writer;

    public bool IsCapturing { get { lock (gate) return writer != null; } }
    public string CurrentPath { get; private set; } = string.Empty;
    public string CaptureId { get; private set; } = string.Empty;

    public DebugCaptureManager(DebugEventStore store, Func<DebugSessionInfo> session, string root)
    {
        this.store = store;
        this.session = session;
        captureRoot = Path.Combine(root, "captures");
        Directory.CreateDirectory(captureRoot);
    }

    public string Start(string name, string captureId = null, TimeSpan? preRoll = null)
    {
        lock (gate)
        {
            StopLocked();
            DebugSessionInfo info = session();
            CaptureId = string.IsNullOrWhiteSpace(captureId) ? Guid.NewGuid().ToString("N") : captureId;
            string safeName = Sanitize(name);
            CurrentPath = Path.Combine(captureRoot, $"{safeName}-{info.Role}-{info.SessionId}.jsonl");
            writer = new StreamWriter(CurrentPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(DebugJson.Serialize(new
            {
                schemaVersion = DebugSessionInfo.CurrentSchemaVersion,
                recordType = "capture-metadata",
                captureId = CaptureId,
                captureName = safeName,
                startedUtc = DateTime.UtcNow,
                session = info
            }));
            DateTime cutoff = DateTime.UtcNow - (preRoll ?? TimeSpan.FromSeconds(2));
            foreach (DebugEvent item in store.Since(cutoff)) writer.WriteLine(DebugJson.Serialize(item));
            store.Published += OnPublished;
            return CurrentPath;
        }
    }

    private void OnPublished(DebugEvent item)
    {
        lock (gate)
        {
            try { writer?.WriteLine(DebugJson.Serialize(item)); } catch { }
        }
    }

    public string Stop() { lock (gate) { string result = CurrentPath; StopLocked(); return result; } }

    private void StopLocked()
    {
        if (writer == null) return;
        store.Published -= OnPublished;
        try { writer.Dispose(); } catch { }
        writer = null;
        CaptureId = string.Empty;
    }

    private static string Sanitize(string name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? "capture" : name.Trim();
        HashSet<char> invalid = new(Path.GetInvalidFileNameChars());
        value = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return value.Length <= 80 ? value : value.Substring(0, 80);
    }

    public void Dispose() { lock (gate) StopLocked(); }
}
