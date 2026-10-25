using System;

namespace Multiplayer.Core.Items.LiveState;

/// <summary>
/// A snap point's stable key, for an item snapped to one that isn't a car's coupler (a hanger, a
/// mount): <c>Home.Snapped</c>'s anchor, saved as the row's <c>snappedTo</c>. The only such snap
/// points in the game are gadgets', so the key is the gadget item's own id, which it keeps for life
/// and across a restart (docs/design/items/live-state.md, "Attached and snapped items").
/// </summary>
public static class SnapPointKey
{
    private const string GadgetPrefix = "gadget:";

    public static string OfGadget(Guid gadgetItemId) =>
        gadgetItemId == Guid.Empty ? string.Empty : GadgetPrefix + gadgetItemId.ToString("D");

    public static bool TryGadget(string key, out Guid gadgetItemId)
    {
        gadgetItemId = Guid.Empty;
        return key != null && key.StartsWith(GadgetPrefix, StringComparison.Ordinal) &&
               Guid.TryParse(key.Substring(GadgetPrefix.Length), out gadgetItemId) && gadgetItemId != Guid.Empty;
    }
}
