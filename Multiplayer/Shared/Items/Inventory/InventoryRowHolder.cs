using Multiplayer.Shared.Items.LiveState;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// Whoever keeps this game's own player's inventory as rows (docs/design/items/inventory.md,
/// "Stand-ins"; D14): a remote client's item manager, or on a player host the server itself, for
/// the host's own player (OI-COLD-11). The put-away and take-out hooks (<see cref="InventoryPutAwayPatch"/>,
/// <see cref="InventoryTakeOutPatch"/>) act through it, the same on either.
/// </summary>
internal interface IInventoryRowHolder
{
    /// <summary>Whether this game's own inventory is stand-ins now.</summary>
    bool Active { get; }

    /// <summary>
    /// Whether an object added straight into a slot (not from a hand) becomes its stand-in as it
    /// goes in. The host's are put away once the server has committed them to their slot instead
    /// (HostInventory.Stow): they come from the server's own flows, which go on using the object
    /// until then.
    /// </summary>
    bool CatchesDirectAdds { get; }

    /// <summary>Whether this object goes into the inventory as a stand-in when the player puts it away.</summary>
    bool PutsAwayAsStandIn(NetworkedItem item);

    /// <summary>The player put an object away and vanilla has its stand-in in a slot: the stash is committed or asked for, and the object goes.</summary>
    void CommitPutAway(NetworkedItem item, InventoryStandIn standIn);

    /// <summary>Takes a stand-in's item out: its object is made in this frame and put where vanilla had the stand-in. The object, or null.</summary>
    NetworkedItem TakeOut(InventoryStandIn standIn, string reason);

    /// <summary>An object vanilla has stored in a slot (moved off the VR belt) becomes its stand-in there.</summary>
    void StowWithoutRequest(NetworkedItem item);
}

internal static class InventoryRowHolder
{
    /// <summary>The server's, for a player host's own player; set by the server's item manager.</summary>
    internal static IInventoryRowHolder Host { get; set; }

    /// <summary>The local player's row holder while its inventory is stand-ins, or null.</summary>
    public static IInventoryRowHolder Current
    {
        get
        {
            if (NetworkedItemManager.Instance?.ClientPart is IInventoryRowHolder client && client.Active)
                return client;
            return Host is { Active: true } host ? host : null;
        }
    }
}
