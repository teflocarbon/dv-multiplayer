using DV.Scenarios.Common;
using DV.UIFramework;
using HarmonyLib;
using Multiplayer.Server.Career;
using Multiplayer.Shared.Session;

namespace Multiplayer.Menus.MainMenu;

/// <summary>
/// Loading a save from the main menu starts the server (and the host's own client) before the
/// world loads, or goes back to the menu with a popup if it can't.
/// </summary>
[HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.FindStartGameData))]
public static class SaveGameManager_FindStartGameData_Patch
{
    private static void Postfix(AStartGameData __result)
    {
        if (NetworkLifecycle.Instance.IsSessionRunning)
            return;
        StartServer(__result.DifficultyToUse);
        // Before DV places the player or loads any item: every player's state is by GUID.
        if (NetworkLifecycle.Instance.IsServerRunning)
            PerPlayerSaveFormat.PrepareLoad(SaveGameManager.Instance.data);
    }

    private static void StartServer(IDifficulty difficulty)
    {
        if (MultiplayerSession.Instance.StartServer(difficulty))
            return;

        NetworkLifecycle.Instance.QueueMainMenuEvent(() =>
        {
            Popup popup = MainMenuThingsAndStuff.Instance.ShowOkPopup();
            if (popup == null)
                return;
            popup.labelTMPro.text = "Failed to start server! Ensure that the port is not in use and try again.";
        });

        DV.UI.MainMenu.GoBackToMainMenu();
    }
}
