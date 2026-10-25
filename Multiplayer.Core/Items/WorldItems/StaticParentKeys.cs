using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Core.Items.WorldItems;

/// <summary>
/// What a shelf's key (an <c>ItemStaticParent</c>, docs/design/items/world-items.md, "Shelves") is
/// made of, the same in every game (OI-SYNC-09, SH1): its path by names alone. Never its absolute
/// position (a moved shelf is still the same shelf), a sibling index or the root DV moves scene
/// content under: under that root, a tile's place is the order the tiles happened to load in, so one
/// game's key for a shelf was another's for nothing. Shelves with one key (two copies of a building,
/// two same-named shelves in one) are told apart by which is nearer the item (<see cref="Nearest"/>).
/// Keys from before R7 aren't read (G3).
/// </summary>
public static class StaticParentKeys
{
    /// <summary>The root DV moves streamed content under (WorldMover.OriginShiftParent).</summary>
    public const string OriginShiftRoot = "[origin shift content]";

    /// <param name="namesFromRoot">The shelf's transform's name and its ancestors', root first.</param>
    public static string Material(IEnumerable<string> namesFromRoot)
    {
        StringBuilder material = new("static-parent-v2");
        bool first = true;
        foreach (string name in namesFromRoot ?? Enumerable.Empty<string>())
        {
            bool shiftRoot = first && string.Equals(name, OriginShiftRoot, StringComparison.Ordinal);
            first = false;
            if (!shiftRoot)
                material.Append('/').Append(name ?? string.Empty);
        }
        return material.ToString();
    }

    /// <summary>Of the shelves with one key, the index of the nearest to the item (squared distances), or -1.</summary>
    public static int Nearest(IReadOnlyList<float> distancesSquared)
    {
        int best = -1;
        for (int i = 0; i < (distancesSquared?.Count ?? 0); i++)
            if (best < 0 || distancesSquared[i] < distancesSquared[best])
                best = i;
        return best;
    }
}
