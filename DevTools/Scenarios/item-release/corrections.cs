// class probe, host. Counts the server's rejection corrections per item, by reason.
// Args: action=install|read|remove, item (read: that item's count).
using HarmonyLib;
public static class Probe
{
    const string Slot = "dvmp.item-release.corrections";
    static Dictionary<ushort, List<string>> Counts
    {
        get
        {
            var c = AppDomain.CurrentDomain.GetData(Slot) as Dictionary<ushort, List<string>>;
            if (c == null) AppDomain.CurrentDomain.SetData(Slot, c = new Dictionary<ushort, List<string>>());
            return c;
        }
    }
    public static void Post(NetworkedItem __instance, string rejectionReason)
    {
        lock (Counts)
        {
            if (!Counts.TryGetValue(__instance.NetId, out var reasons)) Counts[__instance.NetId] = reasons = new List<string>();
            reasons.Add(rejectionReason ?? string.Empty);
        }
    }
    public static object Run(DevContext ctx)
    {
        var h = new Harmony(Slot);
        switch (ctx.Args["action"])
        {
            case "install":
                h.UnpatchAll(Slot);
                h.Patch(AccessTools.Method(typeof(NetworkedItem), "SendCanonicalRejectionCorrection"),
                    postfix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(Post))));
                Counts.Clear();
                return new { installed = true };
            case "remove":
                h.UnpatchAll(Slot);
                return new { removed = true };
            default:
                ushort id = ushort.Parse(ctx.Args["item"]);
                lock (Counts)
                {
                    var reasons = Counts.TryGetValue(id, out var r) ? r.ToList() : new List<string>();
                    return new { count = reasons.Count, reasons = string.Join(",", reasons.Distinct()) };
                }
        }
    }
}
