using DV.Interaction;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.World;

[HarmonyPatch(typeof(KeyUse), nameof(KeyUse.HandleUse))]
public static class KeyUsePatch
{
    private static bool Prefix()
    {
        return NetworkLifecycle.Instance.IsHost();
    }
}
