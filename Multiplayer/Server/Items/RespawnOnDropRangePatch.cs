using HarmonyLib;
using Multiplayer.Server.Players;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items;

/// <summary>
/// On the server, a networked item is "near the player" for DV's <see cref="RespawnOnDrop"/> if it's
/// near any player, not only this game's own one. Vanilla measures from
/// <c>PlayerManager.PlayerTransform</c>: on a dedicated host that's the parked body, where no player
/// stands, and on a player host it's only the host. <see cref="RespawnOnDropPatch"/> leaves a personal
/// item nobody owns yet (one a shop or a command puts beside a client) to vanilla, which then sent it
/// to the host's own Lost and Found a second after it appeared while the server's record still said
/// World: every client got its position as zero and lost its copy.
/// </summary>
[HarmonyPatch(typeof(RespawnOnDrop), nameof(RespawnOnDrop.CheckDistances))]
internal static class RespawnOnDropRangePatch
{
    [HarmonyPostfix]
    private static void AfterCheckDistances(RespawnOnDrop __instance,
        ref (bool farFromSpawn, bool farFromPlayer, bool farFromActiveCamera, bool boundToPlayer) __result)
    {
        // Only ever near where vanilla said far: the host's own player counts as before.
        if (!__result.farFromPlayer || NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsServerRunning)
            return;
        if (!NetworkedItem.TryGetNetworkedItem(__instance.item, out NetworkedItem networked) ||
            networked == null || networked.NetId == 0)
            return;
        if (__instance.transform.position.AnyPlayerSqrMag() <= __instance.maxDistanceSquared)
            __result.farFromPlayer = false;
    }
}
