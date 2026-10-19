// class probe, host. Counts the packets the host's own client drops (NetworkClient.IgnoreOnHost:
// the server sent the host's client something it must never follow), by type.
// Args: action=install|read|remove.
using HarmonyLib;
public static class Probe
{
    const string Slot = "dvmp.host-throw-settles.ignored";
    static Dictionary<string, int> Counts
    {
        get
        {
            var c = AppDomain.CurrentDomain.GetData(Slot) as Dictionary<string, int>;
            if (c == null) AppDomain.CurrentDomain.SetData(Slot, c = new Dictionary<string, int>());
            return c;
        }
    }
    public static void Post(Type packetType)
    {
        lock (Counts)
        {
            string name = packetType?.Name ?? "null";
            Counts.TryGetValue(name, out int n);
            Counts[name] = n + 1;
        }
    }
    public static object Run(DevContext ctx)
    {
        var h = new Harmony(Slot);
        switch (ctx.Args["action"])
        {
            case "install":
                h.UnpatchAll(Slot);
                h.Patch(AccessTools.Method(typeof(Multiplayer.Client.Session.NetworkClient), "IgnoreOnHost"),
                    postfix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(Post))));
                lock (Counts) Counts.Clear();
                return new { installed = true };
            case "remove":
                h.UnpatchAll(Slot);
                return new { removed = true };
            default:
                lock (Counts)
                    return new
                    {
                        total = Counts.Values.Sum(),
                        items = Counts.Where(p => p.Key.Contains("Item")).Sum(p => p.Value),
                        byType = string.Join(",", Counts.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))
                    };
        }
    }
}
