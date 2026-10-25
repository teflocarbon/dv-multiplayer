using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items.Projection;

/// <summary>
/// The world cells a player's item interest is made of (docs/design/items/projection.md, OI-SYNC-06).
/// The world is cut into square cells of <see cref="CellSize"/> metres. An item comes into a player's
/// interest within <see cref="EnterRadius"/> cells of theirs (3x3) and leaves it only beyond
/// <see cref="KeepRadius"/> (5x5): the hysteresis. Without it, walking along a cell boundary destroyed
/// and re-created everything on the other side each time the player stepped across it.
/// </summary>
public static class InterestCells
{
    public const float CellSize = 128f;
    public const int EnterRadius = 1;
    public const int KeepRadius = 2;

    /// <summary>The cell a coordinate (x or z, absolute metres) falls in.</summary>
    public static int CellOf(float coordinate) => (int)Math.Floor(coordinate / CellSize);

    /// <summary>
    /// The items a player is to have: those within <see cref="EnterRadius"/> cells, and those they
    /// already have within <see cref="KeepRadius"/>. <paramref name="itemsWithin"/> gives the items
    /// within a radius of the player's cell; <paramref name="has"/> says whether they have one now.
    /// </summary>
    public static HashSet<T> Wanted<T>(Func<int, HashSet<T>> itemsWithin, bool hasAny, Func<T, bool> has)
    {
        HashSet<T> wanted = itemsWithin(EnterRadius);
        if (hasAny)
            wanted.UnionWith(itemsWithin(KeepRadius).Where(has));
        return wanted;
    }
}
