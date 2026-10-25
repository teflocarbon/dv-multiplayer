using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.Projection;

/// <summary>
/// Which loose items the host's interest finds by the world cell they lie in (docs/design/items/
/// projection.md, "Interest"). An item on a shelf (a static parent) lies where it is, as an item on
/// the ground does: it is indexed by its cell. Before R7 a spatial sample or commit onto a shelf
/// took it out of the cells, and no other rule made it relevant to anyone, so every client was sent
/// its retirement 0.3 s after it landed and never saw it again (SH2, F42, OI-SYNC-04). A train
/// item follows its car instead, and the rest are a player's, a container's or a tool's.
/// </summary>
public static class WorldItemIndexing
{
    public static bool IndexedByCell(LivePlacement placement) =>
        placement is LivePlacement.World or LivePlacement.StaticParent;
}
