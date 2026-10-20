using System;

namespace Multiplayer.Shared.Session;

/// <summary>
///     What the server and the client need from the main menu, which they may not name
///     (docs/design/code-layout.md). The mod's root hands the menus' answers in when it loads.
/// </summary>
public static class FrontEnd
{
    private static Func<string> buildVersion;
    private static Action returnToMainMenu;

    public static void Attach(Func<string> buildVersion, Action returnToMainMenu)
    {
        FrontEnd.buildVersion = buildVersion;
        FrontEnd.returnToMainMenu = returnToMainMenu;
    }

    /// <summary>
    ///     The game's build version as its main menu gives it: a client logs in with it, the server
    ///     takes only its own and advertises it. Read once the main menu has opened.
    /// </summary>
    public static string BuildVersion => buildVersion();

    /// <summary>The session ended under the player (the client lost its server): back to the main menu.</summary>
    public static void ReturnToMainMenu() => returnToMainMenu();
}
