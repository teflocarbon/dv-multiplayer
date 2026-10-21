using LiteNetLib;
using Multiplayer.Server.Gadgets;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Transport;

namespace Multiplayer.Server.Session;

// NetworkServer's gadget handlers and sends (see docs/design/gadgets.md).
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
        GadgetRunningServer.SendAll(player, this);
        Log($"Sent {player.Username} {snapshot.ItemCreates.Count} installed gadgets on " +
            $"{snapshot.Packet.Anchors.Length} cars");
    }

    /// <summary>An installed gadget's settings, to one player or (null) every player who has its gadgets.</summary>
    internal void SendGadgetState(ClientboundGadgetStatePacket packet, ServerPlayer to = null)
    {
        if (to == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForCustomizers, excludeSelf: true);
        else if (to.Peer != SelfPeer)
            SendPacket(to.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>An installed gadget's running state, to one player or (null) every player who has its gadgets.</summary>
    internal void SendGadgetRunning(ClientboundGadgetRunningPacket packet, ServerPlayer to = null)
    {
        if (to == null)
            SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, PlayerLoadingState.ReadyForCustomizers, excludeSelf: true);
        else if (to.Peer != SelfPeer)
            SendPacket(to.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundGadgetSettingPacket(ServerboundGadgetSettingPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetStateServer.HandleRequest(packet, player, this);
    }

    private void OnServerboundGadgetCommandPacket(ServerboundGadgetCommandPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetRunningServer.HandleCommand(packet, player, this);
    }
}
