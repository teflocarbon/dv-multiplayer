// Returns the recorded mutation history for items. Args: ids (comma list, optional; default:
// every item with history), last (entries per item, default 20).
public static class Probe
{
    public static object Run(DevContext ctx)
    {
        if (!DevContext.State.TryGetValue("dvmp.trace.history", out object h) || !(h is Dictionary<ushort, Queue<string>> history))
            return new { installed = DevContext.State.ContainsKey("dvmp.trace.installed"), items = new object[0] };
        int last = ctx.Args.TryGetValue("last", out string l) ? int.Parse(l) : 20;
        HashSet<ushort> ids = ctx.Args.TryGetValue("ids", out string s) && !string.IsNullOrWhiteSpace(s)
            ? new HashSet<ushort>(s.Split(',').Select(ushort.Parse))
            : null;
        return new
        {
            installed = true,
            items = history.Where(p => ids == null || ids.Contains(p.Key))
                .OrderBy(p => p.Key)
                .Select(p => new { id = p.Key, events = p.Value.Skip(Math.Max(0, p.Value.Count - last)).ToList() })
                .ToList()
        };
    }
}
