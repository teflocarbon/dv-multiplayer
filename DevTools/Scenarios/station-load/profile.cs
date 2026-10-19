// class probe. Inclusive timing of the methods suspected in the station-arrival stall.
// Args: action=install|report|reset. Totals live in an AppDomain slot so later evals can read them.
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
public static class Probe
{
    const string Slot = "dvmp.station.profile";
    static Dictionary<string, double[]> Totals
    {
        get
        {
            var t = AppDomain.CurrentDomain.GetData(Slot) as Dictionary<string, double[]>;
            if (t == null) AppDomain.CurrentDomain.SetData(Slot, t = new Dictionary<string, double[]>());
            return t;
        }
    }
    public static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();
    public static void Post(long __state, MethodBase __originalMethod)
    {
        double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        string key = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
        lock (Totals)
        {
            if (!Totals.TryGetValue(key, out var v)) Totals[key] = v = new double[3];
            v[0] += ms; v[1]++; v[2] = Math.Max(v[2], ms);
        }
    }
    static IEnumerable<MethodBase> Targets()
    {
        Type T(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
        foreach (var m in AccessTools.GetDeclaredMethods(typeof(CarSpawner)).Where(m => m.Name == "SpawnCars" || m.Name == "SpawnCar")) yield return m;
        yield return AccessTools.Method(typeof(TrainCar), "Awake");
        yield return AccessTools.Method(typeof(TrainCar), "Start");
        yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(StationProceduralJobsController), "GenerateProceduralJobsCoro"));
        var ntc = T("Multiplayer.Shared.Trains.NetworkedTrainCar");
        yield return AccessTools.Method(ntc, "Awake");
        yield return AccessTools.Method(ntc, "Start");
        yield return AccessTools.Method(T("Multiplayer.Server.Session.NetworkServer"), "SendSpawnTrainset");
        var ncs = T("Multiplayer.Client.Trains.NetworkedCarSpawner");
        yield return AccessTools.Method(ncs, "SpawnCars");
        yield return AccessTools.Method(ncs, "SpawnCar");
        yield return AccessTools.Method(T("Multiplayer.Client.Session.NetworkClient"), "OnClientboundSpawnTrainSetPacket");
        foreach (var m in AccessTools.GetDeclaredMethods(T("Multiplayer.Shared.Items.NetworkedItemManager")).Where(m => m.Name == "OnTick" || m.Name == "UpdatePlayerItemLists" || m.Name == "ProcessChanged")) yield return m;
    }
    public static object Run(DevContext ctx)
    {
        var h = new Harmony("dvmp.station.profile");
        switch (ctx.Args["action"])
        {
            case "install":
                h.UnpatchAll("dvmp.station.profile");
                var pre = new HarmonyMethod(typeof(Probe).GetMethod(nameof(Pre)));
                var post = new HarmonyMethod(typeof(Probe).GetMethod(nameof(Post)));
                var done = new List<string>();
                foreach (var m in Targets().Where(m => m != null)) { h.Patch(m, pre, post); done.Add(m.DeclaringType.Name + "." + m.Name); }
                Totals.Clear();
                return done;
            case "reset": Totals.Clear(); return "reset";
            case "remove": h.UnpatchAll("dvmp.station.profile"); return "removed";
            default:
                return Totals.OrderByDescending(kv => kv.Value[0]).Select(kv => $"{kv.Key}: {kv.Value[0]:F0} ms total, {kv.Value[1]} calls, max {kv.Value[2]:F0} ms").ToList();
        }
    }
}
