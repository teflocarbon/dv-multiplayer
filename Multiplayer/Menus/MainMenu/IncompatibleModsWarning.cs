using Multiplayer.Shared.Api;

namespace Multiplayer.Menus.MainMenu;

public static class IncompatibleModsWarning
{
    /// <summary>
    /// Checks if any incompatible mods are enabled and generates an message box to alert the user
    /// </summary>
    /// <returns>True if incompatible mods have been found</returns>
    public static bool Show()
    {
        var incompatible = ModCompatibilityManager.Instance.EnabledIncompatibleMods();

        if (incompatible.Count == 0)
            return false;

        var message = $"{Locale.MAIN_MENU__INCOMPATIBLE_MODS} {string.Join(", ", incompatible)}";

        MainMenuThingsAndStuff.Instance.ShowOkPopup(message, () => { });

        return true;
    }
}
