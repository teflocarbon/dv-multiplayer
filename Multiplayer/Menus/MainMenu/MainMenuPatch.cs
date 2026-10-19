using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Menus.MainMenu;

[HarmonyPatch(typeof(DV.UI.MainMenu), nameof(DV.UI.MainMenu.GoBackToMainMenu))]
public static class MainMenu_GoBackToMainMenu_Patch
{
    private static void Prefix()
    {
        NetworkLifecycle.Instance.Stop();
    }
}
