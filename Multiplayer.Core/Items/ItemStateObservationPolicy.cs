namespace Multiplayer.Core.Items;

/// <summary>
/// Decides when a process must preserve an authoritative remote player's placement because
/// Derail Valley exposes only the local player's real inventory. Remote hand/inventory objects
/// are multiplayer projections and cannot be recomputed through the local Inventory singleton.
/// </summary>
public static class ItemStateObservationPolicy
{
    public static bool PreserveRemotePlayerPlacement(byte possessorPlayerId,
        byte localPlayerId, WireItemState lastState) =>
        localPlayerId != 0 && possessorPlayerId != 0 && possessorPlayerId != localPlayerId &&
        lastState is WireItemState.InHand or WireItemState.InInventory;
}
