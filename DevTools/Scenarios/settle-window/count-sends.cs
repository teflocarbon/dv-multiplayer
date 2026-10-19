// Either peer: counts calls to NetworkClient send methods, by method and car NetId (their first
// argument). Args: methods (comma separated, e.g. SendTrainSyncRequest,SendAddCoal), action
// (install: patch them if not yet and reset the counts; read: the counts so far).
// The patch stays for the life of the game; counts live in DevContext.State.
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Session;

public static class Probe
{
    private const string Key = "dvmp.scenario.sends";

    public static object Run(DevContext ctx)
    {
        var methods = ctx.Args["methods"].Split(',');
        if (ctx.Args["action"] == "install")
        {
            var harmony = new Harmony("dvmp.scenario.count-sends");
            var prefix = new HarmonyMethod(typeof(Probe).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static));
            foreach (string name in methods)
            {
                if (DevContext.State.ContainsKey(Key + ".patched." + name))
                    continue;
                MethodInfo method = typeof(NetworkClient).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Exception("no NetworkClient." + name);
                harmony.Patch(method, prefix: prefix);
                DevContext.State[Key + ".patched." + name] = true;
            }
            DevContext.State[Key] = new Dictionary<string, int>();
        }
        var counts = DevContext.State.TryGetValue(Key, out var c) ? (Dictionary<string, int>)c : new Dictionary<string, int>();
        return new { counts = counts.Where(kv => methods.Any(m => kv.Key.StartsWith(m + ":"))).ToDictionary(kv => kv.Key, kv => kv.Value) };
    }

    private static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        if (!DevContext.State.TryGetValue(Key, out var c))
            return;
        var counts = (Dictionary<string, int>)c;
        string key = __originalMethod.Name + ":" + (__args.Length > 0 ? __args[0] : "");
        counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
    }
}
