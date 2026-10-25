using System.Collections.Generic;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using UnityEngine;

namespace Multiplayer.Shared.Items.LiveState;

/// <summary>
///     The server's part of <see cref="NetworkedItemManager"/> (<c>Server/Items/Projection/ServerItemManager</c>),
///     as the Shared manager drives it and its Shared callers reach it. It exists on the server only;
///     <see cref="NetworkedItemManager.ServerPart"/> is null on a client.
/// </summary>
public interface IItemManagerServerPart
{
    /// <summary>Every frame, before the Shared spatial manager: train wake, the host's leases, replenishment.</summary>
    void Update();

    /// <summary>Every tick on the host, after the authored catalogue is built.</summary>
    void Tick(uint tick);

    /// <summary>The manager is being destroyed: the session's server item state goes with it.</summary>
    void Clear();

    void RegisterHostWorldItem(NetworkedItem item);
    void UnregisterHostWorldItem(NetworkedItem item);
    void RefreshHostSpatialPosition(NetworkedItem item, Vector3 absolutePosition, ItemWorldParentKind parentKind);

    /// <summary>Drops the server's authority record of an item that's being destroyed.</summary>
    void ForgetRecord(ushort itemNetId);

    /// <summary>An item is going: the train-wake manager wakes what it was holding up.</summary>
    void OnTrainItemRemoved(ushort itemNetId);

    /// <summary>The host's own game saw a train item it simulates hit another.</summary>
    void ReceiveLocalTrainWakeWitness(ServerboundItemTrainWakeWitnessPacket packet);

    void AppendTrainWakeDebugState(ushort itemNetId, Dictionary<string, object> state);

    /// <summary>The server's record has the item installed (InstalledItems).</summary>
    bool IsInstalled(ushort itemNetId);
}
