// Starts hosting from the main menu, through DV's own launcher so the normal
// ContinueGameRequested pipeline (which Multiplayer hooks to start its server) runs.
// Args: save = save name or UID (optional; defaults to the save the launcher shows).
using System.Reflection;
using DV.Common;
using DV.UI;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Components.Networking;
using Multiplayer.Components.UI.ServerBrowser;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Managers.Server;
using Multiplayer.Patches.MainMenu;
using MP = Multiplayer.Multiplayer;

public static class Probe
{
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
        if (ctx.Args.TryGetValue("save", out string wanted) && !string.IsNullOrWhiteSpace(wanted))
        {
            save = AllSaves(selector).FirstOrDefault(s => s.Name == wanted || s.UID.ToString() == wanted)
                   ?? throw new Exception($"save '{wanted}' not found. Available: " +
                                          string.Join(", ", AllSaves(selector).Take(20).Select(s => $"{s.Name} (uid {s.UID}, {s.Type})")));
            launcher.SetData(save, selector.userProfileProvider, selector.scenariosProvider, null);
        }
        if (save == null) throw new Exception("the launcher has no save selected; pass save=<name or uid>");

        // Never advertise automated test servers: skip lobby-server registration, the public
        // IP lookup and Steam lobby creation that LobbyServerManager.Start performs.
        Harmony harmony = new("dvmp.harness.lobby");
        harmony.UnpatchAll("dvmp.harness.lobby");
        harmony.Patch(AccessTools.Method(typeof(LobbyServerManager), "Start"),
            prefix: new HarmonyMethod(typeof(Probe).GetMethod(nameof(SkipLobby), BindingFlags.NonPublic | BindingFlags.Static)));

        int port = MP.Settings.Port;
        string password = MP.Settings.Password ?? string.Empty;
        lifecycle.serverData = new LobbyServerData
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
        lifecycle.IsSinglePlayer = false;

        launcher.OnRunClicked();

        return new { save = save.Name, uid = save.UID, type = save.Type.ToString(), gameMode = save.GameMode, port, password };
    }

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
