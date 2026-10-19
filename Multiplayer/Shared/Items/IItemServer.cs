using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items;

/// <summary>
///     The server's side of items, as Shared and Client code asks it things without referencing
///     the server's types (docs/design/code-layout.md). <c>NetworkServer</c> implements it, in
///     <c>Server/Session/NetworkServer.Items.cs</c>.
/// </summary>
public interface IItemServer
{
    /// <summary>The host's own player's id; 0 until its loopback login has been accepted.</summary>
    byte LocalPlayerId { get; }

    /// <summary>Whether the item is in the server's Lost and Found.</summary>
    bool IsInLostAndFound(ushort itemNetId);

    /// <summary>The owner the server's record of the item names (0 for nobody); false if it has no record.</summary>
    bool TryGetPersistentOwner(ushort itemNetId, out byte ownerPlayerId);

    /// <summary>Whether the server's world-item persistence saves this item, rather than the vanilla save.</summary>
    bool OwnsWorldPersistence(NetworkedItem item);

    /// <summary>The server's part of a new item manager.</summary>
    IItemManagerServerPart CreateItemManagerPart(NetworkedItemManager manager);
}

public static class ItemServer
{
    /// <summary>This game's server, or null when it isn't one.</summary>
    public static IItemServer Current => NetworkLifecycle.Instance?.LocalServer as IItemServer;
}
