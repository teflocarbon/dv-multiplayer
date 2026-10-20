// Starts hosting from the main menu, through DV's own launcher so the normal
// ContinueGameRequested pipeline (which Multiplayer hooks to start its server) runs.
// Args: save = save name or UID (optional; defaults to the save the launcher shows),
//       port = server port (optional; defaults to the mod's setting),
//       playerName, playerGuid = an identity for the host's own player (optional; defaults to
//       the install's). Patched in memory like join.cs does, so Settings.xml is never touched.
using System.Reflection;
using DV.Common;
using DV.UI;
using HarmonyLib;
using Multiplayer.Menus.MainMenu;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Api;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Session;
using MP = Multiplayer.Multiplayer;
using MpSettings = Multiplayer.Settings;

public static class Probe
{
    private static string playerName;
    private static System.Guid playerGuid;

    public static object Run(DevContext ctx)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance ?? throw new Exception("NetworkLifecycle missing (is Multiplayer loaded?)");
        if (lifecycle.IsServerRunning || lifecycle.IsClientRunning)
            return new { alreadyRunning = true, role = ctx.Role };

        MainMenuController menu = UnityEngine.Object.FindObjectOfType<MainMenuController>()
                                  ?? throw new Exception("main menu not ready");
        LauncherController launcher = menu.rightPaneController.launcherController;
        var selector = menu.rightPaneController.continueLoadNewController;

        ISaveGame save = launcher.saveGame;
        string newMode = ctx.Args.TryGetValue("new", out string m) ? m : null;
        if (newMode is "career" or "sandbox")
        {
            // A brand-new session through DV's own new-game flow (tutorial skipped); the launcher
            // then holds the new game's start data and Run starts it.
            var single = newMode == "career" ? selector.career : selector.freeRoam;
            string name = ctx.Args.TryGetValue("name", out string n) && n.Length > 0 ? n : null;
            IGameSession existing = name == null ? null : single.Things.FirstOrDefault(s => s.Name == name);
            if (existing != null) single.CurrentThing = existing;   // reruns reuse the named session
            else single.CreateNewImpl(name);
            var can = single.scenariosProvider.CanStartNewSession(single.currentSession, single.currentScenario, single.currentDifficulty);
            if (!can.Item1) throw new Exception("DV refuses to start a new session: " + can.Item2);
            // The career/sandbox pane is usually not on screen, so its own start event has no
            // listeners; hand the start data straight to the launcher, as the pane would.
            var data = new DV.UI.PresetEditors.UIStartGameData(single.currentSession, single.currentDifficulty, single.currentScenario, true);
            launcher.SetData(data, selector.userProfileProvider, selector.scenariosProvider, null);
            if (launcher.startGameData == null)
                throw new Exception("the launcher did not accept the new session");
            save = null;
        }
        else if (ctx.Args.TryGetValue("save", out string wanted) && !string.IsNullOrWhiteSpace(wanted))
        {
            // A save name, a UID, or a session name (that session's latest save).
            save = AllSaves(selector).FirstOrDefault(s => s.Name == wanted || s.UID.ToString() == wanted || s.ParentSession?.Name == wanted)
                   ?? throw new Exception($"save '{wanted}' not found. Available: " +
                                          string.Join(", ", AllSaves(selector).Take(20).Select(s => $"{s.Name} (uid {s.UID}, {s.Type})")));
            launcher.SetData(save, selector.userProfileProvider, selector.scenariosProvider, null);
        }
        if (save == null && launcher.startGameData == null)
            throw new Exception("the launcher has no save selected; pass save=<name or uid> or new=career|sandbox");

        // Never advertise automated test servers: skip lobby-server registration, the public
        // IP lookup and Steam lobby creation that LobbyServerManager.Start performs.
        Harmony harmony = new("dvmp.harness.lobby");
        harmony.UnpatchAll("dvmp.harness.lobby");
        harmony.Patch(AccessTools.Method(typeof(LobbyServerManager), "Start"),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(SkipLobby), BindingFlags.NonPublic | BindingFlags.Static)));

        // A host joined by the user's own game (dvmp up --host-only) can't share the install's
        // GUID with it: the login check lets a client with the host's GUID in, and items and saved
        // player data are keyed by it. Installed before Run, so the loopback client logs in with it.
        Harmony identity = new("dvmp.harness.identity");
        identity.UnpatchAll("dvmp.harness.identity");
        if (ctx.Args.TryGetValue("playerGuid", out string g) && !string.IsNullOrEmpty(g))
        {
            playerGuid = System.Guid.Parse(g);
            playerName = ctx.Args.TryGetValue("playerName", out string pn) && pn.Length > 0 ? pn : "DVMP-Host";
            identity.Patch(AccessTools.Method(typeof(MpSettings), nameof(MpSettings.GetUserName)),
                prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(UserName), BindingFlags.NonPublic | BindingFlags.Static)));
            identity.Patch(AccessTools.Method(typeof(MpSettings), nameof(MpSettings.GetGuid)),
                prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(Guid), BindingFlags.NonPublic | BindingFlags.Static)));
        }

        // A named dvmp session passes its own port, so parallel hosts never share one. The mod's
        // settings (shared by every game) are left alone.
        int port = ctx.Args.TryGetValue("port", out string p) ? int.Parse(p) : MP.Settings.Port;
        string password = MP.Settings.Password ?? string.Empty;
        global::Multiplayer.MultiplayerSession.Instance.serverData = new LobbyServerData
        {
            port = port,
            Name = "DVMP harness",
            HasPassword = password.Length > 0,
            Visibility = ServerVisibility.Private,
            GameMode = 0,
            Difficulty = 0,
            TimePassed = "N/A",
            CurrentPlayers = 0,
            MaxPlayers = 4,
            RequiredMods = ModCompatibilityManager.Instance.GetLocalMods(),
            GameVersion = MainMenuControllerPatch.MenuProvider.BuildVersionString,
            MultiplayerVersion = MP.Ver,
            ServerDetails = "Automated DVMP harness session"
        };
        global::Multiplayer.MultiplayerSession.Instance.IsSinglePlayer = false;

        launcher.OnRunClicked();

        string player = playerGuid == System.Guid.Empty ? null : $"{playerName} ({playerGuid})";
        return save != null
            ? new { save = save.Name, uid = save.UID, type = save.Type.ToString(), gameMode = save.GameMode, port, password, player }
            : (object)new { save = "new " + newMode + " session: " + launcher.startGameData.session?.Name, uid = 0, type = "New", gameMode = launcher.startGameData.session?.GameMode, port, password, player };
    }

    private static bool UserName(ref string __result) { __result = playerName; return false; }
    private static bool Guid(ref System.Guid __result) { __result = playerGuid; return false; }

    private static bool SkipLobby(ref IEnumerator __result)
    {
        __result = Enumerable.Empty<object>().GetEnumerator();
        return false;
    }

    private static IEnumerable<ISaveGame> AllSaves(DV.UI.PresetEditors.ContinueLoadNewController selector)
    {
        var sessions = new HashSet<IGameSession>();
        if (selector.career?.CurrentThing != null) sessions.Add(selector.career.CurrentThing);
        if (selector.freeRoam?.CurrentThing != null) sessions.Add(selector.freeRoam.CurrentThing);
        var user = DV.UserManagement.UserManager.Instance?.CurrentUser;
        if (user?.Sessions != null)
            foreach (var modeSessions in user.Sessions.Values)
                if (modeSessions != null)
                    foreach (IGameSession session in modeSessions)
                        if (session != null) sessions.Add(session);
        return sessions.Where(s => s.Saves != null).SelectMany(s => s.Saves).Where(s => s != null)
            .OrderByDescending(s => s.Timestamp);
    }
}
