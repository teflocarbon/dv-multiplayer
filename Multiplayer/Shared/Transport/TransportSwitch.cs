using System;

namespace Multiplayer.Shared.Transport;

/// <summary>
///     Which transport this game uses: Steam's, or in a Debug build only, Valve's open-source
///     GameNetworkingSockets when started with <c>-dvmp-transport gns</c> (docs/testing.md, "GNS
///     pairs"). A release has no other transport and ignores the switch: running without Steam
///     must not be possible in a build players get.
/// </summary>
public static class TransportSwitch
{
#if DEBUG
    public const string Argument = "-dvmp-transport";

    /// <summary>Whether this game runs on GameNetworkingSockets, without Steam.</summary>
    public static bool UseGns { get; } = ReadSwitch();

    private static bool ReadSwitch()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, Argument);
        if (index < 0)
            return false;
        string value = index + 1 < args.Length ? args[index + 1] : "";
        if (value.Equals("gns", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!value.Equals("steam", StringComparison.OrdinalIgnoreCase))
            Multiplayer.LogWarning($"{Argument} '{value}' isn't a transport (gns or steam); using Steam");
        return false;
    }
#else
    public static bool UseGns => false;
#endif

    public static ITransport Create()
    {
#if DEBUG
        if (UseGns)
            return new Gns.GnsTransport();
#endif
        return new SteamWorksTransport();
    }
}
