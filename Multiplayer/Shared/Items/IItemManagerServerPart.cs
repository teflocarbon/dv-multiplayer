using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Multiplayer.Shared.Items;

/// <summary>
///     The server's part of <see cref="NetworkedItemManager"/> (<c>Server/Items/ServerItemManager</c>),
///     as the Shared manager drives it and its Shared callers reach it. It exists on the server only;
///     <see cref="NetworkedItemManager.ServerPart"/> is null on a client.
/// </summary>
public interface IItemManagerServerPart
{
    /// <summary>Every frame, after the spatial and train-wake managers.</summary>
    void Update();

    /// <summary>Every tick on the host, after the authored catalogue is built.</summary>
    void Tick(uint tick);

    /// <summary>The manager is being destroyed: the session's server item state goes with it.</summary>
    void Clear();

    void RegisterHostWorldItem(NetworkedItem item);
    void UnregisterHostWorldItem(NetworkedItem item);
    void RefreshHostSpatialPosition(NetworkedItem item, Vector3 absolutePosition, ItemWorldParentKind parentKind);

    /// <summary>The host's own player recalls an item to an inventory slot.</summary>
    bool RequestHostRecall(NetworkedItem item, int requestedSlot);

    /// <summary>Drops the server's authority record of an item that's being destroyed.</summary>
    void ForgetRecord(ushort itemNetId);
}
