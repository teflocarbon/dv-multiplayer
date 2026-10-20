using DV.UI;
using HarmonyLib;
using Multiplayer.Menus.MainMenu;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Dedicated;

/// <summary>
/// Refuses to start a save whose career a dedicated host on this PC has open (DedicatedHostSaveLock),
/// whether the player pressed Run (single player) or Host. Joining that server is the way in.
/// </summary>
[HarmonyPatch(typeof(LauncherController), nameof(LauncherController.OnRunClicked))]
public static class LauncherControllerSaveLockPatch
{
    private static bool Prefix(LauncherController __instance)
    {
        var session = __instance.saveGame?.ParentSession;
        if (session == null || !DedicatedHostSaveLock.IsLocked(session))
            return true;

        string message = $"A dedicated server on this PC is running \"{session.Name}\". Join that server instead: " +
                         "opening the same career here as well would overwrite its saves.";
        Multiplayer.LogWarning(message);
        if (MainMenuThingsAndStuff.Instance != null)
            MainMenuThingsAndStuff.Instance.ShowOkPopup(message, () => { });
        // Hosting may already have been set up for this run.
        MultiplayerSession.Instance.serverData = null;
        MultiplayerSession.Instance.IsSinglePlayer = true;
        return false;
    }
}
