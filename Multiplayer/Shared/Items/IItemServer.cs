using System;
using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items;

/// <summary>
///     The server's side of items, as Shared and Client code asks it things without referencing
///     the server's types (docs/design/code-layout.md). <c>NetworkServer</c> implements it, in
///     <c>Server/Session/NetworkServer.Items.cs</c>.
/// </summary>
public interface IItemServer
{
    /// <summary>Whether the item is in the server's Lost and Found.</summary>
    bool IsInLostAndFound(ushort itemNetId);

    /// <summary>The owner the server's record of the item names (0 for nobody); false if it has no record.</summary>
    bool TryGetPersistentOwner(ushort itemNetId, out byte ownerPlayerId);

    /// <summary>Where the server's record of the item puts it; false if it has no record.</summary>
    bool TryGetPlacement(ushort itemNetId, out ItemPlacementKind placement);

    /// <summary>A scene-authored item's key is known: its record says so.</summary>
    void MarkAuthoredIdentity(NetworkedItem item);

    /// <summary>An authored item left its slot for good: its record becomes an ordinary persistent item's.</summary>
    void PromoteAuthoredToPersistent(NetworkedItem item);

    /// <summary>
    ///     The item is being destroyed (its OnDestroy): the host logs why, keeps a scene-authored
    ///     item's tombstone, queues the Destroy for the players who know it, drops its record
    ///     and forgets its job-report artifact.
    /// </summary>
    void OnItemDestroyed(NetworkedItem item, bool suppressNetworkDestroy);

    /// <summary>Whether the server's world-item persistence saves this item, rather than the vanilla save.</summary>
    bool OwnsWorldPersistence(NetworkedItem item);

    /// <summary>The server's part of a new item manager.</summary>
    IItemManagerServerPart CreateItemManagerPart(NetworkedItemManager manager);

    /// <summary>
    ///     What a player already owns anywhere the server keeps items (prefab, and the inventory
    ///     slot it holds or -1), sent with their join (OwnedItemsSummary).
    /// </summary>
    IReadOnlyList<(string Prefab, int Slot)> OwnedItems(Guid playerIdentity, byte playerId);

    // Debugging: the in-game observability overlay and entity inspector.
    /// <summary>The server's authority record of an item; null if it has none.</summary>
    Dictionary<string, object> AuthorityDebugState(ushort itemNetId);
    Dictionary<string, object>[] LostAndFoundDebugSnapshot();
    Dictionary<string, object>[] ColdContainerDebugSnapshot();
}

public static class ItemServer
{
    /// <summary>This game's server, or null when it isn't one.</summary>
    public static IItemServer Current => NetworkLifecycle.Instance?.LocalServer as IItemServer;
}
