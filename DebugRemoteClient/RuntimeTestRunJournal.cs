#if DEBUG
using Multiplayer.Debugging.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Multiplayer.DebugClient;

internal sealed class RuntimeTestRunJournal
{
    private readonly object gate = new();
    private readonly string root;
    private readonly int maximumRuns;
    private readonly Dictionary<string, RuntimeTestRunDto> runs =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> serialized =
        new(StringComparer.Ordinal);

    public RuntimeTestRunJournal(string root = null, int maximumRuns = 250)
    {
        this.root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "DVMultiplayer", "test-runs")
            : Path.GetFullPath(root);
        this.maximumRuns = Math.Max(10, maximumRuns);
        Directory.CreateDirectory(this.root);
        Load();
    }

    public RuntimeTestRunDto Get(string requestId)
    {
        lock (gate)
            return runs.TryGetValue(requestId ?? string.Empty, out RuntimeTestRunDto run)
                ? Clone(run) : null;
    }

    public RuntimeTestRunSummaryDto[] List()
    {
        lock (gate)
            return runs.Values.OrderByDescending(run => run.QueuedUtc)
                .Select(Summary).ToArray();
    }

    public void Upsert(RuntimeTestRunDto run)
    {
        if (run == null || string.IsNullOrWhiteSpace(run.RequestId)) return;
        RuntimeTestRunDto snapshot = Clone(run);
        string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented,
            DebugJson.Settings);
        lock (gate)
        {
            if (serialized.TryGetValue(snapshot.RequestId, out string existing) &&
                string.Equals(existing, json, StringComparison.Ordinal))
                return;
            WriteAtomic(PathFor(snapshot.RequestId), json);
            runs[snapshot.RequestId] = snapshot;
            serialized[snapshot.RequestId] = json;
            Trim();
        }
    }

    public static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmp-run-journal-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeTestRunJournal journal = new(root, 10);
            journal.Upsert(new RuntimeTestRunDto
            {
                RequestId = "one", RunId = "run-one", Command = "runtime.self-check",
                CaseId = "runtime.self-check", Status = RuntimeTestCommandStatus.Passed,
                QueuedUtc = DateTime.UtcNow.AddSeconds(-1), StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
                Result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["cleanupClean"] = true,
                    ["captureFiles"] = new[] { "host.jsonl", "client.jsonl" }
                }
            });
            RuntimeTestRunJournal reloaded = new(root, 10);
            RuntimeTestRunSummaryDto summary = reloaded.List().Single();
            if (summary.RequestId != "one" || summary.Status !=
                RuntimeTestCommandStatus.Passed || summary.CleanupClean != true ||
                summary.CaptureFiles.Length != 2 || reloaded.Get("one") == null)
                throw new InvalidOperationException(
                    "Runtime-test run journal failed persistence self-test.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private void Load()
    {
        foreach (string path in Directory.GetFiles(root, "*.json")
                     .OrderBy(File.GetLastWriteTimeUtc))
        {
            try
            {
                RuntimeTestRunDto run = JsonConvert.DeserializeObject<RuntimeTestRunDto>(
                    File.ReadAllText(path), DebugJson.Settings);
                if (run == null || string.IsNullOrWhiteSpace(run.RequestId)) continue;
                bool interrupted = !Terminal(run.Status);
                if (interrupted)
                {
                    run.Status = RuntimeTestCommandStatus.FailedDirty;
                    run.Error = "dashboard-restarted-before-run-completed";
                    run.CompletedUtc = DateTime.UtcNow;
                    run.Result ??= new Dictionary<string, object>(StringComparer.Ordinal);
                    run.Result["cleanupClean"] = false;
                }
                string json = JsonConvert.SerializeObject(run, Formatting.Indented,
                    DebugJson.Settings);
                runs[run.RequestId] = run;
                serialized[run.RequestId] = json;
                if (interrupted) WriteAtomic(path, json);
            }
            catch { }
        }
        lock (gate) Trim();
    }

    private void Trim()
    {
        foreach (RuntimeTestRunDto remove in runs.Values
                     .OrderByDescending(run => run.QueuedUtc)
                     .Skip(maximumRuns).ToArray())
        {
            runs.Remove(remove.RequestId);
            serialized.Remove(remove.RequestId);
            try { File.Delete(PathFor(remove.RequestId)); } catch { }
        }
    }

    private string PathFor(string requestId) => Path.Combine(root,
        Sanitize(requestId) + ".json");

    private static string Sanitize(string value)
    {
        HashSet<char> invalid = new(Path.GetInvalidFileNameChars());
        string safe = new((value ?? string.Empty)
            .Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") :
            safe.Length <= 120 ? safe : safe.Substring(0, 120);
    }

    private static void WriteAtomic(string path, string value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, value);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private static RuntimeTestRunDto Clone(RuntimeTestRunDto run) =>
        JsonConvert.DeserializeObject<RuntimeTestRunDto>(
            JsonConvert.SerializeObject(run, DebugJson.Settings), DebugJson.Settings);

    private static RuntimeTestRunSummaryDto Summary(RuntimeTestRunDto run)
    {
        bool? cleanup = null;
        if (run.Result != null && run.Result.TryGetValue("cleanupClean", out object raw))
            try { cleanup = Convert.ToBoolean(raw, CultureInfo.InvariantCulture); } catch { }
        string[] captures = Array.Empty<string>();
        if (run.Result != null && run.Result.TryGetValue("captureFiles", out raw))
        {
            try
            {
                captures = raw is JArray array ? array.Values<string>().Where(value =>
                    !string.IsNullOrWhiteSpace(value)).ToArray() :
                    raw is IEnumerable<string> strings ? strings.ToArray() :
                    JArray.FromObject(raw).Values<string>().ToArray();
            }
            catch { captures = Array.Empty<string>(); }
        }
        return new RuntimeTestRunSummaryDto
        {
            RequestId = run.RequestId,
            RunId = run.RunId,
            CaseId = run.CaseId,
            Command = run.Command,
            Status = run.Status,
            QueuedUtc = run.QueuedUtc,
            StartedUtc = run.StartedUtc,
            CompletedUtc = run.CompletedUtc,
            PhaseId = run.PhaseId,
            StepId = run.StepId,
            Error = run.Error,
            ProcessCount = run.Processes?.Count ?? 0,
            CleanupClean = cleanup,
            CaptureFiles = captures
        };
    }

    private static bool Terminal(RuntimeTestCommandStatus status) => status is
        RuntimeTestCommandStatus.Passed or RuntimeTestCommandStatus.Failed or
        RuntimeTestCommandStatus.FailedDirty or RuntimeTestCommandStatus.Cancelled or
        RuntimeTestCommandStatus.Unsupported;
}
#endif
