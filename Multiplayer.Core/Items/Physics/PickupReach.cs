using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.Physics;

/// <summary>
/// The server's pickup range check (docs/design/items/physics.md, "Pickup reach"; OI-PHY-08,
/// F35). A player takes an item lying in the world, on a shelf or in a car only from within
/// reach of where the server has it. Before R7 any player who had been sent the item could take
/// it from across the map. The bound is generous: vanilla grabs at about 2 m, but the server sees
/// a player's position a moment late, and a VR player reaches further.
/// </summary>
public static class PickupReach
{
    /// <summary>How far from the item, in metres, the server still takes a player's pickup.</summary>
    public const float Metres = 10f;

    /// <summary>Whether a pickup from <paramref name="placement"/> at this squared distance is in reach.</summary>
    public static bool Within(LivePlacement placement, float distanceSqr) =>
        !ItemLeaseRules.IsSimulatable(placement) || distanceSqr <= Metres * Metres;
}
