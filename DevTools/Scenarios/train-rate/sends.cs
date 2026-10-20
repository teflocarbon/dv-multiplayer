// Host: how many ClientboundTrainsetPhysicsPacket the host sends a second over a window, to each
// player: of the given train, and of every trainset (idle yard cars jittering above vanilla's
// stationary line are sent too; trains.md, Motion, open issues). Harmony prefixes on
// NetworkServer.SendTrainsetPhysicsUpdate (which set's packet is going out) and
// DebugTrace.PacketSending (called on every send whether or not tracing is on).
// A full class probe: run it with `-m class`. Args: seconds, cars (comma-separated IDs).
using System.Reflection;
using HarmonyLib;
using LiteNetLib.Utils;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Shared.Transport;

public static class Probe
{
    private const string HarmonyId = "dvmp.train-rate.sends";
    // peer -> [this train's packets, bytes, every trainset's packets, bytes]
    private static Dictionary<ITransportPeer, long[]> counts;
    private static HashSet<int> trainNetIds;
    private static bool sendingTrain;

    public static object Run(DevContext ctx) => Routine(ctx);

    private static IEnumerator Routine(DevContext ctx)
    {
        float seconds = float.Parse(ctx.Args["seconds"], System.Globalization.CultureInfo.InvariantCulture);
        trainNetIds = new HashSet<int>(ctx.Args["cars"].Split((char)44)
            .Select(id => NetworkedTrainCar.TryGetFromTrainCar(Trains.Find(id), out var net) ? (int)net.NetId : 0));
        counts = new Dictionary<ITransportPeer, long[]>();
        Harmony harmony = new(HarmonyId);
        harmony.UnpatchAll(HarmonyId);
        BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        harmony.Patch(AccessTools.Method(typeof(NetworkServer), nameof(NetworkServer.SendTrainsetPhysicsUpdate)),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(OnSet), flags)),
            postfix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(OnSetDone), flags)));
        harmony.Patch(AccessTools.Method(typeof(DebugTrace), nameof(DebugTrace.PacketSending)),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(OnSending), flags)));
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
        string Name(ITransportPeer peer) => server != null && server.TryGetServerPlayer(peer, out var player) ? player.Username : "peer " + peer.GetHashCode();
        ctx.Result = new
        {
            seconds = Math.Round(elapsed, 1),
            train = window.ToDictionary(p => Name(p.Key), p => Math.Round(p.Value[0] / elapsed, 1)),
            trainBytes = window.ToDictionary(p => Name(p.Key), p => Math.Round(p.Value[1] / elapsed)),
            allTrainsets = window.ToDictionary(p => Name(p.Key), p => Math.Round(p.Value[2] / elapsed, 1)),
            allTrainsetsBytes = window.ToDictionary(p => Name(p.Key), p => Math.Round(p.Value[3] / elapsed)),
        };
    }

    private static void OnSet(ClientboundTrainsetPhysicsPacket packet) =>
        sendingTrain = packet != null && (trainNetIds.Contains(packet.FirstNetId) || trainNetIds.Contains(packet.LastNetId));

    private static void OnSetDone() => sendingTrain = false;

    private static void OnSending(ITransportPeer peer, NetDataWriter writer, string packetType)
    {
        if (peer == null || packetType != "ClientboundTrainsetPhysicsPacket")
            return;
        int bytes = writer?.Length ?? 0;
        lock (counts)
        {
            if (!counts.TryGetValue(peer, out long[] c))
                counts[peer] = c = new long[4];
            if (sendingTrain)
            {
                c[0]++;
                c[1] += bytes;
            }
            c[2]++;
            c[3] += bytes;
        }
    }
}
