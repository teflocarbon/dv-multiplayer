using DV;
using DV.Common;
using DV.UI;
using DV.UI.PresetEditors;
using DV.UserManagement;
using Multiplayer.API;
using Multiplayer.Components.UI.ServerBrowser;
using Multiplayer.Networking.Data;
using Multiplayer.Patches.MainMenu;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking;

/// <summary>
/// Dedicated-host mode, started with <c>-mp-dedicated</c>: the game hosts with nobody playing it.
/// The host keeps its loopback client, which carries its own simulation and applies relayed state
/// to its world, but that client's player is a service identity rather than a player: it has its
/// own GUID, isn't announced or shown, takes no player slot and doesn't count as "a player nearby".
/// DV's own player object stays in the world, because DV can't load or save without one; it is
/// parked where the save left it and sends no position.
/// See docs/design/dedicated-host.md.
///
/// Arguments:
///   -mp-dedicated            the mode itself
///   -mp-save NAME|UID|continue  host this save from the main menu: a save name or UID, a session
///                            name (its latest save), or "continue" (the save the launcher shows).
///                            Without it the mode waits for hosting to be started another way.
///   -mp-port N               server port (default: the mod's Port setting)
///   -mp-visibility private|friends|public  (default: the mod's Visibility setting)
///   -mp-fps N                frame cap, at least 25 (default 30)
///   -mp-autosave N           minutes between saves while anyone is online (default 10)
///   -mp-heartbeat PATH       rewrite PATH every 5 s with the host's state (JSON), for a watchdog
/// Server name, password, max players and details come from the mod's settings.
/// With nobody online the game saves and pauses; admins (the install's own player, and
/// Settings.AdminGuids) have /save, /stop and /payfees in chat (ChatManager).
/// </summary>
public static class DedicatedHost
{
    public const string ModeArg = "-mp-dedicated";

    /// <summary>The service identity's name. It's never shown to clients.</summary>
    public const string ServiceName = "Dedicated host";

    // At or above NetworkLifecycle.TICK_RATE (24), so ticks and polls aren't throttled.
    private const int MinimumFps = 25;

    private static readonly string[] args = Environment.GetCommandLineArgs();

    public static bool Requested { get; } = args.Contains(ModeArg);

    private static bool autoHostTried;

    public static string Arg(string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[i + 1] : null;
    }

    public static int FpsCap => int.TryParse(Arg("-mp-fps"), out int fps) ? Math.Max(fps, MinimumFps) : 30;

    /// <summary>Minutes between saves while anyone is online (-mp-autosave, default 10).</summary>
    public static int AutosaveMinutes => int.TryParse(Arg("-mp-autosave"), out int minutes) ? Math.Max(1, minutes) : 10;

    /// <summary>A file the host rewrites every few seconds with its state, for a watchdog (-mp-heartbeat).</summary>
    public static string HeartbeatPath => Arg("-mp-heartbeat");

    public static DateTime? LastSave { get; private set; }
    public static string LastSaveReason { get; private set; }

    // The game is paused while nobody is online; this is the request that holds it.
    private static readonly object emptyServerPause = new();

    /// <summary>
    /// Called when a player has logged in or left, and once the world has loaded. With nobody
    /// online the game saves (if someone just left) and pauses, so time, jobs and loose trains
    /// don't run on unwatched; the first login unpauses it.
    /// </summary>
    public static void OnPlayersChanged(bool someoneLeft)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (!lifecycle.IsDedicatedHost)
            return;
        if (lifecycle.Server.GameplayPlayerCount > 0)
            AppUtil.Instance.RemovePauseRequest(emptyServerPause);
        else
            lifecycle.StartCoroutine(SaveAndPauseWhenEmpty(someoneLeft));
    }

    /// <summary>Whether the game may pause: only while a dedicated host has nobody online.</summary>
    public static bool AllowsPause => NetworkLifecycle.Instance.IsDedicatedHost && NetworkLifecycle.Instance.Server.GameplayPlayerCount == 0;

    private static IEnumerator SaveAndPauseWhenEmpty(bool save)
    {
        // Let the disconnect finish (it updates the save data with the departing player first).
        yield return null;
        if (!AllowsPause)
            yield break;
        if (save)
            Save("the last player left");
        AppUtil.Instance.RequestPause(emptyServerPause, true);
        Multiplayer.Log("Dedicated host: nobody online, paused");
    }

    /// <summary>Saves the game now. Returns whether DV wrote a save.</summary>
    public static bool Save(string reason)
    {
        if (SaveGameManager.Instance == null || !SaveGameManager.Instance.SaveAllowed())
        {
            Multiplayer.LogWarning($"Dedicated host: not saving ({reason}): the game doesn't allow saving right now");
            return false;
        }
        ISaveGame save = SaveGameManager.Instance.Save(DV.Common.SaveType.Auto);
        bool saved = save != null;
        if (saved)
        {
            LastSave = DateTime.UtcNow;
            LastSaveReason = reason;
            BackUp(save);
        }
        Multiplayer.Log(saved ? $"Dedicated host: saved ({reason})" : $"Dedicated host: saving failed ({reason})");
        return saved;
    }

    /// <summary>Minutes between backup copies (-mp-backup-minutes, default 60; 0 turns them off).</summary>
    public static int BackupMinutes => int.TryParse(Arg("-mp-backup-minutes"), out int minutes) ? Math.Max(0, minutes) : 60;

    /// <summary>How many backup copies to keep (-mp-backups, default 48).</summary>
    public static int BackupsKept => int.TryParse(Arg("-mp-backups"), out int count) ? Math.Max(1, count) : 48;

    private static DateTime lastBackup = DateTime.MinValue;

    // DV's Auto saves rotate through a few slots, so frequent saves push old ones out. Every
    // BackupMinutes a copy of the newest goes to DedicatedBackups in the career's folder, outside
    // that rotation, keeping the newest BackupsKept.
    private static void BackUp(ISaveGame save)
    {
        if (BackupMinutes == 0 || (DateTime.UtcNow - lastBackup).TotalMinutes < BackupMinutes)
            return;
        try
        {
            if (save is not DV.UserManagement.Data.SaveGameSnapshot snapshot || string.IsNullOrEmpty(snapshot.BasePath))
                return;
            // Save paths are relative to the user storage's base, the game's persistent data folder.
            string file = System.IO.Path.Combine(Application.persistentDataPath, snapshot.BasePath);
            if (!System.IO.File.Exists(file))
                return;
            string careerDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(file));
            string backups = System.IO.Path.Combine(careerDir, "DedicatedBackups");
            System.IO.Directory.CreateDirectory(backups);
            System.IO.File.Copy(file, System.IO.Path.Combine(backups, $"{DateTime.Now:yyyyMMdd-HHmmss}_{System.IO.Path.GetFileName(file)}"), overwrite: true);
            foreach (var old in new System.IO.DirectoryInfo(backups).GetFiles("*.save")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(BackupsKept))
                old.Delete();
            lastBackup = DateTime.UtcNow;
            Multiplayer.Log($"Dedicated host: backed the save up to {backups}");
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"Dedicated host: backing the save up failed: {e.Message}");
        }
    }

    /// <summary>True once the host has been told to stop; the heartbeat says so, for a watchdog.</summary>
    public static bool Stopping { get; private set; }

    /// <summary>
    /// Saves, tells the players and quits, from /stop or a stop request file. The heartbeat is
    /// marked first, so a watchdog doesn't take the exit for a crash.
    /// </summary>
    public static void Stop(string reason)
    {
        if (Stopping || !NetworkLifecycle.Instance.IsDedicatedHost)
            return;
        Stopping = true;
        bool saved = Save(reason);
        NetworkLifecycle.Instance.Server.ChatManager.ServerMessage(
            saved ? "The server is shutting down; the game is saved." : "The server is shutting down (saving failed).", null);
        Multiplayer.Log($"Dedicated host: stopping ({reason})");
        WriteHeartbeat();
        // A moment for the message to go out before the transport closes.
        NetworkLifecycle.Instance.StartCoroutine(QuitSoon());
    }

    private static IEnumerator QuitSoon()
    {
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit();
    }

    /// <summary>A watchdog asks the host to stop by creating this file (the heartbeat path + ".stop").</summary>
    public static string StopRequestPath => HeartbeatPath == null ? null : HeartbeatPath + ".stop";

    // One JSON object, replaced whole each time, so a reader never sees half of one.
    internal static void WriteHeartbeat()
    {
        if (HeartbeatPath == null)
            return;
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        try
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                time = DateTime.UtcNow.ToString("o"),
                tick = lifecycle.Tick,
                // Unscaled: while paused the scaled frame time is zero.
                fps = Math.Round(1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 1),
                players = lifecycle.IsServerRunning ? lifecycle.Server.GameplayPlayers.Select(player => player.Username).ToArray() : new string[0],
                paused = AppUtil.Instance.IsTimePaused,
                stopping = Stopping,
                lastSave = LastSave?.ToString("o"),
                monoHeapMb = GC.GetTotalMemory(false) / (1024 * 1024),
            });
            string temp = HeartbeatPath + ".tmp";
            System.IO.File.WriteAllText(temp, json);
            if (System.IO.File.Exists(HeartbeatPath)) System.IO.File.Replace(temp, HeartbeatPath, null);
            else System.IO.File.Move(temp, HeartbeatPath);
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"Dedicated host: couldn't write the heartbeat file: {e.Message}");
        }
    }

    /// <summary>Called when the main menu loads. Hosts the -mp-save save once per process.</summary>
    public static void OnMainMenu()
    {
        if (!Requested || autoHostTried || Arg("-mp-save") == null)
            return;
        autoHostTried = true;
        NetworkLifecycle.Instance.StartCoroutine(AutoHost(Arg("-mp-save")));
    }

    private static IEnumerator AutoHost(string wanted)
    {
        // The launcher is wired up a few frames after the menu scene loads.
        LauncherController launcher = null;
        ContinueLoadNewController selector = null;
        float deadline = Time.realtimeSinceStartup + 120f;
        while (Time.realtimeSinceStartup < deadline)
        {
            MainMenuController menu = UnityEngine.Object.FindObjectOfType<MainMenuController>();
            launcher = menu?.rightPaneController?.launcherController;
            selector = menu?.rightPaneController?.continueLoadNewController;
            if (launcher != null && selector?.userProfileProvider != null)
                break;
            yield return null;
        }
        if (launcher == null || selector?.userProfileProvider == null)
        {
            Multiplayer.LogError("Dedicated host: the main menu's launcher never became ready; not hosting");
            yield break;
        }

        ISaveGame save = wanted.Equals("continue", StringComparison.OrdinalIgnoreCase)
            ? launcher.saveGame
            : AllSaves(selector).FirstOrDefault(s => s.Name == wanted || s.UID.ToString() == wanted || s.ParentSession?.Name == wanted);
        if (save == null)
        {
            Multiplayer.LogError($"Dedicated host: save '{wanted}' not found. Available: " +
                                 string.Join(", ", AllSaves(selector).Take(20).Select(s => $"{s.ParentSession?.Name}/{s.Name} (uid {s.UID})")));
            yield break;
        }
        launcher.SetData(save, selector.userProfileProvider, selector.scenariosProvider, null);

        int port = int.TryParse(Arg("-mp-port"), out int p) ? p : Multiplayer.Settings.Port;
        ServerVisibility visibility = Enum.TryParse(Arg("-mp-visibility"), true, out ServerVisibility v) ? v : Multiplayer.Settings.Visibility;
        string password = Multiplayer.Settings.Password ?? string.Empty;
        NetworkLifecycle.Instance.serverData = new LobbyServerData
        {
            port = port,
            Name = string.IsNullOrWhiteSpace(Multiplayer.Settings.ServerName) ? "Dedicated server" : Multiplayer.Settings.ServerName,
            HasPassword = password.Length > 0,
            Visibility = visibility,
            GameMode = LobbyServerData.GetGameModeFromString(save.GameMode),
            Difficulty = 0,
            TimePassed = "N/A",
            CurrentPlayers = 0,
            MaxPlayers = Multiplayer.Settings.MaxPlayers,
            RequiredMods = ModCompatibilityManager.Instance.GetLocalMods(),
            GameVersion = MainMenuControllerPatch.MenuProvider.BuildVersionString,
            MultiplayerVersion = Multiplayer.Ver,
            ServerDetails = Multiplayer.Settings.Details,
        };
        NetworkLifecycle.Instance.IsSinglePlayer = false;

        Multiplayer.Log($"Dedicated host: hosting '{save.ParentSession?.Name}/{save.Name}' on port {port} ({visibility})");
        launcher.OnRunClicked();
    }

    private static IEnumerable<ISaveGame> AllSaves(ContinueLoadNewController selector)
    {
        HashSet<IGameSession> sessions = new();
        if (selector.career?.CurrentThing != null) sessions.Add(selector.career.CurrentThing);
        if (selector.freeRoam?.CurrentThing != null) sessions.Add(selector.freeRoam.CurrentThing);
        var user = UserManager.Instance?.CurrentUser;
        if (user?.Sessions != null)
            foreach (var modeSessions in user.Sessions.Values)
                if (modeSessions != null)
                    foreach (IGameSession session in modeSessions)
                        if (session != null) sessions.Add(session);
        return sessions.Where(s => s.Saves != null).SelectMany(s => s.Saves).Where(s => s != null)
            .OrderByDescending(s => s.Timestamp);
    }
}

/// <summary>
/// Keeps a dedicated host cheap to run: muted, lowest graphics, small textures, a frame cap and
/// running in the background. Nothing is written to the game's settings. DV re-applies its own
/// preferences on scene loads, so the values are re-asserted.
/// </summary>
public sealed class DedicatedHostMode : MonoBehaviour
{
    private float nextGraphicsPass;
    private float nextSave;
    private float nextHeartbeat;

    private void Update()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (!lifecycle.IsDedicatedHost)
            return;
        float now = Time.realtimeSinceStartup;

        // Timed saves while anyone is online; the last player leaving saves too.
        if (lifecycle.Server.GameplayPlayerCount == 0 || AppUtil.Instance.IsTimePaused)
            nextSave = 0f;
        else if (nextSave == 0f)
            nextSave = now + DedicatedHost.AutosaveMinutes * 60f;
        else if (now >= nextSave)
        {
            nextSave = now + DedicatedHost.AutosaveMinutes * 60f;
            DedicatedHost.Save("timed");
        }

        if (DedicatedHost.HeartbeatPath != null && now >= nextHeartbeat)
        {
            nextHeartbeat = now + 5f;
            DedicatedHost.WriteHeartbeat();
            // A watchdog's graceful stop (e.g. a daily restart): save and quit as /stop does.
            try
            {
                if (System.IO.File.Exists(DedicatedHost.StopRequestPath))
                {
                    System.IO.File.Delete(DedicatedHost.StopRequestPath);
                    DedicatedHost.Stop("stop requested by the watchdog");
                }
            }
            catch (Exception e)
            {
                Multiplayer.LogWarning($"Dedicated host: couldn't read the stop request: {e.Message}");
            }
        }
    }

    private void Awake()
    {
        // Before the world loads, so textures are read at the reduced size in the first place.
        ApplyGraphics();
    }

    private void LateUpdate()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = DedicatedHost.FpsCap;
        Application.runInBackground = true;
        AudioListener.volume = 0f;

        if (Time.unscaledTime >= nextGraphicsPass)
        {
            nextGraphicsPass = Time.unscaledTime + 5f;
            ApplyGraphics();
        }
    }

    private static void ApplyGraphics()
    {
        if (QualitySettings.GetQualityLevel() != 0) QualitySettings.SetQualityLevel(0, applyExpensiveChanges: true);
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.pixelLightCount = 0;
        QualitySettings.lodBias = 0.3f;
        QualitySettings.antiAliasing = 0;
        if (QualitySettings.masterTextureLimit != 2) QualitySettings.masterTextureLimit = 2;
        // DV sets about 16.9 GB, so streamed textures are never trimmed.
        if (!Mathf.Approximately(QualitySettings.streamingMipmapsMemoryBudget, 512f)) QualitySettings.streamingMipmapsMemoryBudget = 512f;
    }
}
