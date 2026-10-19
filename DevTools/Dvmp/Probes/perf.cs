// Performance sample: frame times over a window, memory (including the retained Mono heap),
// physics and world counts, players, and packets per type sent and received in the window.
// Args: seconds = sampling window (default 10), retained = "false" to skip the forced GC that
//       measures the retained heap (it costs a frame hitch).
// Packet counters are Harmony prefixes on the mod's per-packet debug hooks (DebugTrace), which
// run on every send and receive whether or not the debug system is on. They're installed on the
// first run and kept in DevContext.State, so later runs report the window since they started.
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using LiteNetLib.Utils;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;

public static class Probe
{
    private const string HarmonyId = "dvmp.perf.packets";
    private const string StateKey = "dvmp.perf.packets";

    // name -> [count, bytes]; shared across probe compiles through DevContext.State.
    private static Dictionary<string, long[]> counters;
    private static Dictionary<ulong, string> packetNames;

    public static object Run(DevContext ctx) => Routine(ctx);

    private static IEnumerator Routine(DevContext ctx)
    {
        float seconds = float.Parse(Arg(ctx, "seconds", "10"), System.Globalization.CultureInfo.InvariantCulture);
        bool retained = Arg(ctx, "retained", "true") != "false";
        bool installedNow = InstallCounters();

        Dictionary<string, long[]> before;
        lock (counters) before = counters.ToDictionary(p => p.Key, p => (long[])p.Value.Clone());
        int[] gcBefore = Enumerable.Range(0, System.GC.MaxGeneration + 1).Select(System.GC.CollectionCount).ToArray();
        long tickBefore = NetworkLifecycle.Instance?.Tick ?? 0u;

        List<float> frames = new();
        float start = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - start < seconds)
        {
            yield return null;
            frames.Add(Time.unscaledDeltaTime * 1000f);
        }
        float elapsed = Time.realtimeSinceStartup - start;
        long ticks = (NetworkLifecycle.Instance?.Tick ?? 0u) - tickBefore;
        int[] gcAfter = Enumerable.Range(0, System.GC.MaxGeneration + 1).Select(System.GC.CollectionCount).ToArray();

        Dictionary<string, long[]> after;
        lock (counters) after = counters.ToDictionary(p => p.Key, p => (long[])p.Value.Clone());
        var packets = after
            .Select(p => new { key = p.Key, count = p.Value[0] - (before.TryGetValue(p.Key, out long[] b) ? b[0] : 0), bytes = p.Value[1] - (before.TryGetValue(p.Key, out long[] b2) ? b2[1] : 0) })
            .Where(p => p.count > 0)
            .OrderByDescending(p => p.bytes)
            .ToList();

        long heapBefore = System.GC.GetTotalMemory(false);
        long? retainedHeap = null;
        if (retained)
        {
            // Collect, run finalizers, collect again: what is left is reachable, so growth across
            // runs is a leak rather than GC sawtooth.
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();
            retainedHeap = System.GC.GetTotalMemory(false);
        }

        frames.Sort();
        Rigidbody[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
        List<TrainCar> cars = Dev.All<TrainCar>();
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;

        ctx.Result = new
        {
            role = ctx.Role,
            windowSeconds = Math.Round(elapsed, 1),
            countersInstalledNow = installedNow,
            frames = new
            {
                count = frames.Count,
                fps = Math.Round(frames.Count / elapsed, 1),
                p50Ms = Percentile(frames, 0.50),
                p95Ms = Percentile(frames, 0.95),
                p99Ms = Percentile(frames, 0.99),
                maxMs = frames.Count == 0 ? 0 : Math.Round(frames[frames.Count - 1], 1),
                targetFrameRate = Application.targetFrameRate,
            },
            ticks = new { perSecond = Math.Round(ticks / elapsed, 1), tick = lifecycle?.Tick },
            memoryMb = new
            {
                workingSet = Mb(ProcessMemory(out long privateBytes)),
                privateBytes = Mb(privateBytes),
                unityAllocated = Mb(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()),
                unityReserved = Mb(UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong()),
                monoHeap = Mb(heapBefore),
                monoRetained = retainedHeap is long r ? Mb(r) : (long?)null,
                textures = Mb((long)Texture.currentTextureMemory),
            },
            gcCollections = gcAfter.Select((n, i) => n - gcBefore[i]).ToArray(),
            world = new
            {
                rigidbodies = bodies.Length,
                awakeRigidbodies = bodies.Count(b => !b.isKinematic && !b.IsSleeping()),
                cars = cars.Count,
                awakeCars = cars.Count(c => c.rb != null && !c.rb.isKinematic && !c.rb.IsSleeping()),
                trainsets = Trainset.allSets.Count,
                items = Dev.All<NetworkedItem>().Count,
            },
            players = new
            {
                server = lifecycle?.IsServerRunning == true ? lifecycle.Server.ServerPlayers.Select(p => p.Username).ToArray() : null,
                remoteAvatars = Dev.All<Multiplayer.Components.Networking.Player.NetworkedPlayer>().Count,
            },
            packets = new
            {
                sentPerSecond = Math.Round(packets.Where(p => p.key.StartsWith("send ")).Sum(p => p.count) / elapsed, 1),
                sentBytesPerSecond = Math.Round(packets.Where(p => p.key.StartsWith("send ")).Sum(p => p.bytes) / elapsed),
                receivedPerSecond = Math.Round(packets.Where(p => p.key.StartsWith("recv ")).Sum(p => p.count) / elapsed, 1),
                receivedBytesPerSecond = Math.Round(packets.Where(p => p.key.StartsWith("recv ")).Sum(p => p.bytes) / elapsed),
                byType = packets.Take(25).Select(p => new { p.key, perSecond = Math.Round(p.count / elapsed, 1), bytesPerSecond = Math.Round(p.bytes / elapsed) }).ToArray(),
            },
        };
    }

    // Returns true when the counters were installed by this run (the window then starts empty).
    private static bool InstallCounters()
    {
        bool fresh = !DevContext.State.TryGetValue(StateKey, out object existing);
        counters = fresh ? new Dictionary<string, long[]>() : (Dictionary<string, long[]>)existing;
        DevContext.State[StateKey] = counters;
        packetNames = AccessTools.Field(typeof(DebugTrace), "packetNames").GetValue(null) as Dictionary<ulong, string>
                      ?? new Dictionary<ulong, string>();

        // Re-point the prefixes at this compile's methods (and its static fields).
        Harmony harmony = new(HarmonyId);
        harmony.UnpatchAll(HarmonyId);
        BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        harmony.Patch(AccessTools.Method(typeof(DebugTrace), nameof(DebugTrace.PacketSending)),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(OnSending), flags)));
        harmony.Patch(AccessTools.Method(typeof(DebugTrace), nameof(DebugTrace.PacketReceived)),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(OnReceived), flags)));
        return fresh;
    }

    private static void OnSending(NetDataWriter writer, DebugRuntimeSide side, string packetType)
    {
        if (writer == null) return;
        string name = packetType ?? (writer.Length >= sizeof(ulong) && packetNames.TryGetValue(BitConverter.ToUInt64(writer.Data, 0), out string n) ? n : "Unknown");
        Count($"send {side} {name}", writer.Length);
    }

    private static void OnReceived(NetDataReader reader, DebugRuntimeSide side)
    {
        if (reader == null) return;
        int count = reader.AvailableBytes;
        string name = count >= sizeof(ulong) && packetNames.TryGetValue(BitConverter.ToUInt64(reader.RawData, reader.Position), out string n) ? n : "Unknown";
        Count($"recv {side} {name}", count);
    }

    private static void Count(string key, int bytes)
    {
        lock (counters)
        {
            if (!counters.TryGetValue(key, out long[] c)) counters[key] = c = new long[2];
            c[0]++;
            c[1] += bytes;
        }
    }

    private static double Percentile(List<float> sorted, double p) =>
        sorted.Count == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)], 1);

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    // Mono's Process.WorkingSet64 reads 0 in the game, so ask Windows directly.
    private static long ProcessMemory(out long privateBytes)
    {
        ProcessMemoryCounters pmc = new() { cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
        privateBytes = 0;
        if (!K32GetProcessMemoryInfo(GetCurrentProcess(), ref pmc, pmc.cb)) return 0;
        privateBytes = (long)pmc.PrivateUsage;
        return (long)pmc.WorkingSetSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    private static string Arg(DevContext ctx, string key, string fallback) =>
        ctx.Args.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : fallback;
}
