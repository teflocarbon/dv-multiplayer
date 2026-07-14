namespace Multiplayer.Core.Items;

/// <summary>
/// Decides when a listen server must preserve an authoritative remote player's placement
/// because that player's inventory and hand are not represented by the host's local game.
/// The host's own inventory must always be observed from Unity.
/// </summary>
public static class ItemStateObservationPolicy
{
    public static bool PreserveRemotePlayerPlacement(bool isHost, byte possessorPlayerId,
        byte hostPlayerId, WireItemState lastState) =>
        isHost && possessorPlayerId != 0 && possessorPlayerId != hostPlayerId &&
        lastState is WireItemState.InHand or WireItemState.InInventory;
}
