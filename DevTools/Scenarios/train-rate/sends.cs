// Host: how many ClientboundTrainsetPhysicsPacket the host sends a second over a window, in all and
// to each player, from a Harmony prefix on DebugTrace.PacketSending (called on every send whether
// or not tracing is on). A full class probe: run it with `-m class`. Args: seconds.
using System.Reflection;
using HarmonyLib;
using LiteNetLib.Utils;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;

public static class Probe
{
    private const string HarmonyId = "dvmp.train-rate.sends";
    private static Dictionary<ITransportPeer, long[]> counts;

    public static object Run(DevContext ctx) => Routine(ctx);

    private static IEnumerator Routine(DevContext ctx)
    {
        float seconds = float.Parse(ctx.Args["seconds"], System.Globalization.CultureInfo.InvariantCulture);
        counts = new Dictionary<ITransportPeer, long[]>();
        Harmony harmony = new(HarmonyId);
        harmony.UnpatchAll(HarmonyId);
        harmony.Patch(AccessTools.Method(typeof(DebugTrace), nameof(DebugTrace.PacketSending)),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(OnSending), BindingFlags.NonPublic | BindingFlags.Static)));
        float start = Time.realtimeSinceStartup;
        try
        {
            while (Time.realtimeSinceStartup - start < seconds)
                yield return null;
        }
        finally
        {
            harmony.UnpatchAll(HarmonyId);
        }
        float elapsed = Time.realtimeSinceStartup - start;
        var server = NetworkLifecycle.Instance.Server;
        Dictionary<ITransportPeer, long[]> window;
        lock (counts) window = counts.ToDictionary(p => p.Key, p => (long[])p.Value.Clone());
        ctx.Result = new
        {
            seconds = Math.Round(elapsed, 1),
            perSecond = Math.Round(window.Values.Sum(c => c[0]) / elapsed, 1),
            bytesPerSecond = Math.Round(window.Values.Sum(c => c[1]) / elapsed),
            players = window.ToDictionary(
                p => server != null && server.TryGetServerPlayer(p.Key, out var player) ? player.Username : "peer " + p.Key.GetHashCode(),
                p => Math.Round(p.Value[0] / elapsed, 1)),
        };
    }

    private static void OnSending(ITransportPeer peer, NetDataWriter writer, string packetType)
    {
        if (peer == null || packetType != "ClientboundTrainsetPhysicsPacket")
            return;
        lock (counts)
        {
            if (!counts.TryGetValue(peer, out long[] c))
                counts[peer] = c = new long[2];
            c[0]++;
            c[1] += writer?.Length ?? 0;
        }
    }
}
