using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Multiplayer.Debugging.Protocol;

public sealed class AsyncJsonlSink : IDisposable
{
    private readonly BlockingCollection<DebugEvent> queue;
    private readonly Thread worker;
    private readonly string path;
    private long dropped;

    public string Path => path;
    public long Dropped => Interlocked.Read(ref dropped);

    public AsyncJsonlSink(DebugEventStore store, string path, int queueCapacity = 4096)
    {
        this.path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        queue = new BlockingCollection<DebugEvent>(new ConcurrentQueue<DebugEvent>(), Math.Max(256, queueCapacity));
        worker = new Thread(WriteLoop) { IsBackground = true, Name = "DVMP Debug JSONL" };
        store.Published += OnPublished;
        worker.Start();
        Store = store;
    }

    private DebugEventStore Store { get; }
    private void OnPublished(DebugEvent item) { if (!queue.TryAdd(item)) Interlocked.Increment(ref dropped); }

    private void WriteLoop()
    {
        try
        {
            using StreamWriter writer = new(path, true, new UTF8Encoding(false)) { AutoFlush = false };
            int pending = 0;
            foreach (DebugEvent item in queue.GetConsumingEnumerable())
            {
                writer.WriteLine(DebugJson.Serialize(item));
                if (++pending >= 32) { writer.Flush(); pending = 0; }
            }
            writer.Flush();
        }
        catch { }
    }

    public void Dispose()
    {
        Store.Published -= OnPublished;
        queue.CompleteAdding();
        worker.Join(2000);
        queue.Dispose();
    }
}

public sealed class DebugDiscoveryFile : IDisposable
{
    private readonly object gate = new();
    private readonly Func<DebugSessionInfo> session;
    private readonly string path;
    private readonly Timer timer;

    public string Path => path;

    public DebugDiscoveryFile(string root, Func<DebugSessionInfo> session)
    {
        this.session = session;
        string directory = System.IO.Path.Combine(root, "sessions");
        Directory.CreateDirectory(directory);
        path = System.IO.Path.Combine(directory, session().SessionId + ".json");
        Write();
        timer = new Timer(_ => Write(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Write()
    {
        lock (gate)
        {
            try
            {
                DebugSessionInfo info = session();
                info.HeartbeatUtc = DateTime.UtcNow;
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, DebugJson.Serialize(info), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            catch { }
        }
    }

    public static bool IsLive(DebugSessionInfo info, DateTime nowUtc, TimeSpan maximumHeartbeatAge)
    {
        if (info == null || nowUtc - info.HeartbeatUtc > maximumHeartbeatAge) return false;
        try
        {
            using Process process = Process.GetProcessById(info.ProcessId);
            return Math.Abs((process.StartTime.ToUniversalTime() - info.ProcessStartedUtc).TotalSeconds) < 2;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        timer.Dispose();
        lock (gate) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
