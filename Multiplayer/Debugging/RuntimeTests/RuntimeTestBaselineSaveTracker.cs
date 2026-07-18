#if DEBUG
using DV.Common;
using DV.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace Multiplayer.Debugging.RuntimeTests;

/// <summary>
/// Records the exact save object carried through DV's main-menu launcher and
/// AStartGameData pipeline.  Selecting a save in the dashboard is not sufficient
/// evidence: DV falls back to CurrentSession.LatestSave when the intended start
/// data is missing, which can silently load an autosave from an earlier test.
/// </summary>
internal static class RuntimeTestBaselineSaveTracker
{
    private static readonly object Gate = new();
    private static ISaveGame requestedSave;
    private static LauncherController configuredLauncher;
    private static ISaveGame launcherSave;
    private static bool launcherSaveVerified;
    private static ISaveGame startDataSave;

    internal static void Begin(ISaveGame save)
    {
        lock (Gate)
        {
            requestedSave = save;
            configuredLauncher = null;
            launcherSave = null;
            launcherSaveVerified = false;
            startDataSave = null;
        }
    }

    internal static void RecordLauncher(LauncherController launcher, ISaveGame save)
    {
        lock (Gate)
        {
            if (requestedSave == null)
                return;
            configuredLauncher = launcher;
            launcherSave = save;
            // Preserve the result after leaving the main menu. Unity destroys the
            // LauncherController during scene load, so testing its Unity null
            // state later would incorrectly erase a previously verified handoff.
            launcherSaveVerified = SameSave(requestedSave, save);
        }
    }

    internal static void RecordStartData(ISaveGame save)
    {
        lock (Gate)
        {
            if (requestedSave != null)
                startDataSave = save;
        }
    }

    internal static bool TryGetVerifiedLauncher(out LauncherController launcher,
        out ISaveGame configuredSave)
    {
        lock (Gate)
        {
            launcher = configuredLauncher;
            configuredSave = launcherSave;
            return launcher != null && SameSave(requestedSave, launcherSave);
        }
    }

    internal static bool IsStartDataVerified(out ISaveGame actualSave)
    {
        lock (Gate)
        {
            actualSave = startDataSave;
            return SameSave(requestedSave, startDataSave);
        }
    }

    internal static void AddStatus(IDictionary<string, object> result)
    {
        if (result == null)
            return;
        lock (Gate)
        {
            result["baselineRequested"] = requestedSave != null;
            result["baselineLauncherVerified"] = launcherSaveVerified;
            result["baselineStartDataVerified"] = SameSave(requestedSave, startDataSave);
            AddDescription(result, "baselineRequested", requestedSave);
            AddDescription(result, "baselineLauncher", launcherSave);
            AddDescription(result, "baselineStartData", startDataSave);
        }
    }

    internal static bool SameSave(ISaveGame expected, ISaveGame actual)
    {
        if (expected == null || actual == null)
            return false;
        return expected.UID == actual.UID &&
               expected.Type == SaveType.Manual && actual.Type == SaveType.Manual &&
               string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) &&
               string.Equals(expected.GameMode, actual.GameMode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(expected.BasePath, actual.BasePath,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(expected.ParentSession?.BasePath,
                   actual.ParentSession?.BasePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDescription(IDictionary<string, object> result,
        string prefix, ISaveGame save)
    {
        result[prefix + "SaveUid"] = save?.UID ?? -1;
        result[prefix + "SaveName"] = save?.Name ?? string.Empty;
        result[prefix + "SaveType"] = save?.Type.ToString() ?? string.Empty;
        result[prefix + "SaveGameMode"] = save?.GameMode ?? string.Empty;
        result[prefix + "SaveBasePath"] = save?.BasePath ?? string.Empty;
        result[prefix + "SessionBasePath"] = save?.ParentSession?.BasePath ?? string.Empty;
    }
}

[HarmonyPatch(typeof(AStartGameData), nameof(AStartGameData.Continue),
    new[] { typeof(ISaveGame), typeof(bool) })]
internal static class RuntimeTestBaselineStartDataPatch
{
    private static void Prefix(ISaveGame __0) =>
        RuntimeTestBaselineSaveTracker.RecordStartData(__0);
}
#endif
