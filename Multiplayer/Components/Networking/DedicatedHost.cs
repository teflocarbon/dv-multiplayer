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
/// Server name, password, max players and details come from the mod's settings.
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
