using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.Physics;

/// <summary>
/// The server's pickup range check (docs/design/items/physics.md, "Pickup reach"; OI-PHY-08,
/// F35). A player takes an item that lies outside every player and container (in the world, on a
/// shelf, in a car, attached or snapped to a car: <see cref="LiesOut"/>) only from within reach of
/// where the server has it. Before R7 any player who had been sent the item could take it from
/// across the map; before the R7 fixes, an end-of-train lantern snapped on a car 250 m away too.
/// The bound is generous: vanilla grabs at about 2 m, but the server sees a player's position a
/// moment late, and a VR player reaches further.
/// </summary>
public static class PickupReach
{
    /// <summary>How far from the item, in metres, the server still takes a player's pickup.</summary>
    public const float Metres = 10f;

    /// <summary>Whether the item lies where a player has to reach it: not in a hand, an inventory, a container or a tool.</summary>
    public static bool LiesOut(LivePlacement placement) =>
        placement is LivePlacement.World or LivePlacement.TrainInterior or LivePlacement.StaticParent or
            LivePlacement.Attached or LivePlacement.SnappedAttachment;

    /// <summary>Whether a pickup from <paramref name="placement"/> at this squared distance is in reach.</summary>
    public static bool Within(LivePlacement placement, float distanceSqr) =>
        !LiesOut(placement) || distanceSqr <= Metres * Metres;
}
