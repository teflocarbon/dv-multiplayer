using System;
using HarmonyLib;
using UnityModManagerNet;

namespace Dvmp.Agent;

/// <summary>
/// Mods (Multiplayer included) log through Unity Mod Manager's logger, which writes to its own
/// sinks and never reaches Application.logMessageReceived. Mirror those lines into the agent's
/// log buffer so dvmp_logs shows them.
/// </summary>
internal static class ModLoggerHook
{
    public static void Install()
    {
        Harmony harmony = new("dvmp.agent.logs");
        harmony.Patch(AccessTools.Method(typeof(UnityModManager.Logger), nameof(UnityModManager.Logger.Log),
                new[] { typeof(string), typeof(string) }),
            postfix: new HarmonyMethod(typeof(ModLoggerHook), nameof(AfterLog)));
    }

    private static void AfterLog(string str, string prefix)
    {
        if (string.IsNullOrEmpty(str)) return;
        string level =
            str.IndexOf("[Error]", StringComparison.Ordinal) >= 0 || str.IndexOf("Exception", StringComparison.Ordinal) >= 0 ? "error" :
            str.IndexOf("[Warning]", StringComparison.Ordinal) >= 0 ? "warning" : "log";
        LogBuffer.Append(level, (prefix ?? string.Empty) + str);
    }
}
