using System;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Integrations.Seasons;

/// <summary>
/// Dynamic Seasons (DVSeasons, Nexus mod 1668, github.com/HerodrineYT/DVSeasons), for /season.
///
/// It syncs itself through the Multiplayer API: the host is its authority and publishes the season,
/// snow, drifts and blizzards to every player. A dedicated host has no screen for its settings, so
/// /season calls its own runtime (DVSeasons.Mod.Main.runtime, a SeasonRuntime): SetSeason,
/// AdvanceToNextSeason, ScheduleBlizzard and EndBlizzard each act only on the authority and then
/// publish. Found by reflection, so this mod doesn't depend on it.
/// </summary>
internal static class DVSeasonsIntegration
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static object Runtime()
    {
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DVSeasons");
        return assembly?.GetType("DVSeasons.Mod.Main")?.GetField("runtime", Any)?.GetValue(null);
    }

    public static bool Installed => Runtime() != null;

    /// <summary>The season and how far into its transition, as DVSeasons reports it, or null.</summary>
    public static string Current()
    {
        object state = Runtime()?.GetType().GetProperty("CurrentState", Any)?.GetValue(Runtime());
        if (state == null)
            return null;
        object season = state.GetType().GetProperty("Current", Any)?.GetValue(state);
        object transition = state.GetType().GetProperty("Transition", Any)?.GetValue(state);
        return transition is float t && t > 0.01f ? $"{season} (turning, {t:P0})" : season?.ToString();
    }

    public static bool TrySetSeason(string name, out string error)
    {
        error = string.Empty;
        object runtime = Runtime();
        MethodInfo set = runtime?.GetType().GetMethod("SetSeason", Any);
        Type kind = set?.GetParameters().FirstOrDefault()?.ParameterType;
        if (runtime == null || set == null || kind == null || !kind.IsEnum)
        {
            error = "Dynamic Seasons isn't installed on the server, or this version can't be driven.";
            return false;
        }
        string match = Enum.GetNames(kind).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ??
                       (string.Equals(name, "fall", StringComparison.OrdinalIgnoreCase) ? "Autumn" : null);
        if (match == null)
        {
            error = $"No season called {name}: {string.Join(", ", Enum.GetNames(kind)).ToLowerInvariant()}.";
            return false;
        }
        set.Invoke(runtime, new[] { Enum.Parse(kind, match) });
        return true;
    }

    public static bool TryCall(string method, out string error)
    {
        error = string.Empty;
        object runtime = Runtime();
        MethodInfo call = runtime?.GetType().GetMethod(method, Any, null, Type.EmptyTypes, null);
        if (call == null)
        {
            error = "Dynamic Seasons isn't installed on the server, or this version can't be driven.";
            return false;
        }
        call.Invoke(runtime, null);
        return true;
    }
}
