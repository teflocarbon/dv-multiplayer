using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
/// The item code never destroys an installed item, on any game (docs/design/item-refactor.md, "The
/// gadget seam"; OI-GAD-06, F49): an installed gadget's item is the gadget code's, which takes it
/// out of <see cref="ItemState.Installed"/> first when it goes (a removal, a deleted car's Lost and
/// Found). Each place item code destroys an item asks <see cref="RefuseDestroy"/> first.
/// </summary>
internal static class InstalledItems
{
    /// <summary>
    /// True, logged, if the item is installed and must not be destroyed by <paramref name="by"/>.
    /// The server goes by its record; a remote client by the last state it was sent.
    /// </summary>
    internal static bool RefuseDestroy(NetworkedItem item, string by)
    {
        if (item == null || !IsInstalled(item))
            return false;
        Multiplayer.LogWarning($"Refused to destroy installed item {item.name} (netId {item.NetId}): {by}");
        DebugRuntime.Publish("item", "item.destroy-refused", NetworkRole.DebugSide, DebugSeverity.Warning,
            "Item", item.NetId.ToString(), new()
            {
                ["by"] = by ?? string.Empty,
                ["reason"] = "installed"
            });
        return true;
    }

    internal static bool IsInstalled(NetworkedItem item) =>
        NetworkedItemManager.Instance?.ServerPart is { } server
            ? item.NetId != 0 && server.IsInstalled(item.NetId)
            : item.LastObservedState == ItemState.Installed;
}
