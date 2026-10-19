using System;
using System.Reflection;

namespace Dvmp.Agent;

/// <summary>
/// Reads Multiplayer's session state through reflection so the agent has no compile-time
/// dependency on Multiplayer.dll and keeps working even if that mod fails to load.
/// </summary>
internal static class Mp
{
    private static bool resolved;
    private static PropertyInfo instanceProp;
    private static MethodInfo isHostMethod;
    private static PropertyInfo serverRunningProp;
    private static PropertyInfo clientRunningProp;
    private static PropertyInfo serverProp;
    private static PropertyInfo tickProp;

    private static void Resolve()
    {
        if (resolved) return;
        resolved = true;
        Type lifecycle = Type.GetType("Multiplayer.Shared.Session.NetworkLifecycle, Multiplayer");
        if (lifecycle == null) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        instanceProp = lifecycle.GetProperty("Instance", flags);
        isHostMethod = lifecycle.GetMethod("IsHost", Type.EmptyTypes);
        serverRunningProp = lifecycle.GetProperty("IsServerRunning");
        clientRunningProp = lifecycle.GetProperty("IsClientRunning");
        serverProp = lifecycle.GetProperty("Server");
        tickProp = lifecycle.GetProperty("Tick");
    }

    private static object Instance()
    {
        Resolve();
        try { return instanceProp?.GetValue(null); }
        catch { return null; }
    }

    public static bool Loaded
    {
        get { Resolve(); return instanceProp != null; }
    }

    public static string Role()
    {
        object lifecycle = Instance();
        if (lifecycle == null) return "offline";
        try
        {
            bool clientRunning = (bool)(clientRunningProp?.GetValue(lifecycle) ?? false);
            bool serverRunning = (bool)(serverRunningProp?.GetValue(lifecycle) ?? false);
            if (serverRunning) return "host";
            if (clientRunning) return (bool)(isHostMethod?.Invoke(lifecycle, null) ?? false) ? "host" : "client";
            return "offline";
        }
        catch { return "unknown"; }
    }

    public static object Snapshot()
    {
        object lifecycle = Instance();
        if (lifecycle == null) return new { loaded = Loaded, role = "offline" };
        try
        {
            object server = serverProp?.GetValue(lifecycle);
            object playerCount = server?.GetType().GetProperty("PlayerCount")?.GetValue(server);
            return new
            {
                loaded = true,
                role = Role(),
                serverRunning = serverRunningProp?.GetValue(lifecycle),
                clientRunning = clientRunningProp?.GetValue(lifecycle),
                playerCount,
                tick = tickProp?.GetValue(lifecycle)
            };
        }
        catch (Exception e)
        {
            return new { loaded = true, role = "unknown", error = e.Message };
        }
    }
}
