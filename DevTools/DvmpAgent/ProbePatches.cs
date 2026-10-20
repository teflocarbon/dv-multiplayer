using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Dvmp.Agent;

/// <summary>
/// The Harmony patches probes left in this process: every owner id starting with "dvmp.", plus
/// any patch whose method lives in a probe assembly whatever its owner. Main thread only.
/// </summary>
internal static class ProbePatches
{
    /// <summary>Never removed by `dvmp probes`: live hotfixes and the agent's own hooks.</summary>
    public static bool IsProtected(string owner) =>
        owner.StartsWith("dvmp.live.", StringComparison.Ordinal) || owner.StartsWith("dvmp.agent.", StringComparison.Ordinal);

    /// <summary>Patches the harness itself relies on (identity, lobby); only removed when named exactly.</summary>
    public static bool IsHarness(string owner) => owner.StartsWith("dvmp.harness.", StringComparison.Ordinal);

    public sealed class Owner
    {
        public string Id;
        public string Category;
        public int Methods;
        public SortedSet<string> Kinds = new();
        public List<string> Sample = new();
        public bool FromProbe;
    }

    public static List<Owner> Snapshot()
    {
        Dictionary<string, Owner> owners = new();
        foreach (MethodBase method in Harmony.GetAllPatchedMethods().ToList())
        {
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) continue;
            HashSet<string> seenHere = new();
            void Add(IEnumerable<Patch> patches, string kind)
            {
                foreach (Patch p in patches)
                {
                    string id = p.owner ?? string.Empty;
                    bool fromProbe = ProbeRegistry.IsProbeAssembly(p.PatchMethod?.DeclaringType?.Assembly);
                    if (!fromProbe && !id.StartsWith("dvmp.", StringComparison.Ordinal)) continue;
                    if (!owners.TryGetValue(id, out Owner o))
                        owners[id] = o = new Owner { Id = id, Category = Category(id) };
                    o.FromProbe |= fromProbe;
                    o.Kinds.Add(kind);
                    if (seenHere.Add(id))
                    {
                        o.Methods++;
                        if (o.Sample.Count < 5) o.Sample.Add(method.DeclaringType?.Name + "." + method.Name);
                    }
                }
            }
            Add(info.Prefixes, "prefix");
            Add(info.Postfixes, "postfix");
            Add(info.Transpilers, "transpiler");
            Add(info.Finalizers, "finalizer");
        }
        return owners.Values.OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
    }

    private static string Category(string id) =>
        IsProtected(id) ? (id.StartsWith("dvmp.live.", StringComparison.Ordinal) ? "live" : "agent")
        : IsHarness(id) ? "harness"
        : "probe";

    public static object Describe(Owner o) => new
    {
        owner = o.Id,
        category = o.Category,
        methods = o.Methods,
        kinds = o.Kinds.ToArray(),
        sample = o.Sample,
        fromProbeAssembly = o.FromProbe
    };

    /// <summary>
    /// Removes the named owners' patches. "all" means every probe-category owner (never live,
    /// agent or harness ones); a harness owner must be named exactly. Only owners in the snapshot
    /// can be removed, so this can't unpatch the Multiplayer mod or another mod.
    /// </summary>
    public static (List<string> Removed, List<string> Refused) Unpatch(IEnumerable<string> selectors)
    {
        List<Owner> current = Snapshot();
        List<string> removed = new(), refused = new();
        foreach (string raw in selectors)
        {
            string selector = raw?.Trim();
            if (string.IsNullOrEmpty(selector)) continue;
            IEnumerable<Owner> targets = selector == "all"
                ? current.Where(o => o.Category == "probe")
                : current.Where(o => o.Id == selector || (selector.EndsWith("*") && o.Id.StartsWith(selector.TrimEnd('*'), StringComparison.Ordinal) && o.Category == "probe"));
            List<Owner> list = targets.ToList();
            if (list.Count == 0) { refused.Add(selector + ": no such probe patch owner"); continue; }
            foreach (Owner o in list)
            {
                if (IsProtected(o.Id)) { refused.Add(o.Id + ": protected (" + o.Category + ")"); continue; }
                if (string.IsNullOrEmpty(o.Id)) { refused.Add("(patches with no owner id): can't be removed by owner"); continue; }
                // Always pass the id: UnpatchAll(null) would strip every mod's patches.
                new Harmony(o.Id).UnpatchAll(o.Id);
                if (!removed.Contains(o.Id)) removed.Add(o.Id);
            }
        }
        return (removed, refused);
    }

    public static int ProbeAssemblyCount() =>
        AppDomain.CurrentDomain.GetAssemblies().Count(a => ProbeRegistry.IsProbeAssembly(a));
}
