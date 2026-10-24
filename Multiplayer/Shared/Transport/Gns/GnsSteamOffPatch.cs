using HarmonyLib;
using Steamworks;
using System;

namespace Multiplayer.Shared.Transport.Gns;

/// <summary>
///     A game on the GNS transport never starts Steam, so a GNS pair on a PC with Steam running
///     behaves as one on a pod without it: no Steam name, lobby, invites or tickets, and the
///     players keyed by their install GUIDs. DVSteamworks.Awake catches this the way it catches
///     Steam not running, and leaves DVSteamworks.Success false for everything that checks it.
/// </summary>
[HarmonyPatch(typeof(SteamClient), nameof(SteamClient.Init))]
public static class GnsSteamOffPatch
{
    private static bool Prefix()
    {
        if (!TransportSwitch.UseGns)
            return true;
        // DVSteamworks treats this prefix as "Steam isn't there" and logs it without a stack trace.
        throw new Exception("SteamApi_Init failed with -dvmp-transport gns: this game runs without Steam");
    }
}
