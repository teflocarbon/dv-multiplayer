using LiteNetLib;
using Multiplayer.Server.Gadgets;

namespace Multiplayer.Server.Session;

// NetworkServer's gadget sends (see docs/design/gadgets.md).
public partial class NetworkServer
{
    /// <summary>
    /// A player reached <c>ReadyForCustomizers</c>: the installed gadgets' items, then every car's
    /// gadgets (GadgetJoinSnapshot), on the one reliable ordered channel.
    /// </summary>
    private void SendGadgetJoinSnapshot(ServerPlayer player)
    {
        GadgetJoinSnapshot snapshot = GadgetJoinSnapshot.Build(player);
        if (snapshot.ItemCreates.Count > 0)
            SendItemsBulkUpdatePacket(snapshot.ItemCreates, player);
        SendPacket(player.Peer, snapshot.Packet, DeliveryMethod.ReliableOrdered);
        Log($"Sent {player.Username} {snapshot.ItemCreates.Count} installed gadgets on " +
            $"{snapshot.Packet.Anchors.Length} cars");
    }
}
