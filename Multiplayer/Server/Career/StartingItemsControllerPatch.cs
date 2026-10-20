using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Career;

/// <summary>
/// While loading, DV adds any starting items (comms radio, maps, compass, wallet, ...) and licence
/// papers missing from the items it was given (StartingItemsSafeguard, LicensesSafeguard). In
/// multiplayer a player's items come back from the server, so DV only sees part of them; without
/// this it made a fresh set on every join, duplicating the ones the server hands back.
///
/// The list DV combines from every storage is used only by those two safeguards, so the items the
/// player already owns are added to it as present, and DV adds only what's really missing, as in
/// single player:
/// - a client: what the server says the player owns (Client/Career/StartingItemsClientPatch);
/// - a player host: what its own player owns according to the save (Server/Career/StartingItemsServerPatch).
/// A dedicated host's local player is a parked body, not a player, so it gets nothing.
/// </summary>
[HarmonyPatch(typeof(StartingItemsController))]
public static class StartingItemsControllerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(StartingItemsController.StartingItemsSafeguard))]
    private static bool StartingItemsPrefix() => !NetworkLifecycle.Instance.IsDedicatedHost;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(StartingItemsController.LicensesSafeguard))]
    private static bool LicensesPrefix() => !NetworkLifecycle.Instance.IsDedicatedHost;
}
