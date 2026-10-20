using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Career;

/// <summary>
/// Only the host unlocks garages and the like; a client gets them from the host
/// (<c>ClientboundGarageUnlockPacket</c>).
/// </summary>
[HarmonyPatch(typeof(UnlockablesManager), nameof(UnlockablesManager.UnlockInArray))]
public static class UnlockablesManagerPatch
{
    private static bool Prefix()
    {
        return !NetworkRole.IsRemoteClient;
    }
}
