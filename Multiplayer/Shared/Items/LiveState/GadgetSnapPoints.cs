using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.Items.Snapping;
using UnityEngine;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
/// A gadget's snap point (a hanger, a mount), the only one that isn't a coupler or a belt, named on
/// the wire by its gadget item's NetId (<see cref="ItemWorldParentKind.SnapPoint"/>) and in the
/// ledger by the gadget item's id (<c>SnapPointKey</c>; docs/design/items/live-state.md, "Attached
/// and snapped items").
/// </summary>
internal static class GadgetSnapPoints
{
    /// <summary>The NetId of the gadget item whose snap point this is, or 0 (a coupler, a belt, none).</summary>
    public static ushort GadgetOf(ItemSnapPointBase point) =>
        point is SnapPointGadget && point.GetComponentInParent<GadgetBase>() is { } gadget &&
        gadget.GadgetItem != null && gadget.GadgetItem.GetComponent<NetworkedItem>() is { NetId: not 0 } item
            ? item.NetId : (ushort)0;

    /// <summary>The snap point of the installed gadget whose item has this NetId, or null.</summary>
    public static SnapPointGadget PointOf(ushort gadgetItemNetId) =>
        gadgetItemNetId != 0 && NetworkedItem.TryGet(gadgetItemNetId, out NetworkedItem item) && item != null &&
        item.GetComponent<GadgetItem>() is { Gadget: GadgetWithSnapPoint { snapPoint: { } point } } ? point : null;
}
