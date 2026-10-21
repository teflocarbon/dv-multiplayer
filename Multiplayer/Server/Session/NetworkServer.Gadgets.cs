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

    private void OnServerboundGadgetPanelPacket(ServerboundGadgetPanelPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetPanelServer.Handle(packet, player);
    }

    /// <summary>The world has loaded: the gadget change log records from now (before, the snapshot covers it).</summary>
    internal bool WorldLoaded => IsLoaded;

    /// <summary>One car's gadget changes, to one player (the change log sends each player its own, after its Creates).</summary>
    internal void SendGadgetChanges(ClientboundGadgetChangesPacket packet, ServerPlayer to) =>
        SendPacket(to.Peer, packet, DeliveryMethod.ReliableOrdered);

    internal void SendGadgetAnchorSnapshot(ClientboundGadgetAnchorSnapshotPacket packet, ServerPlayer to) =>
        SendPacket(to.Peer, packet, DeliveryMethod.ReliableOrdered);

    internal void SendGadgetReject(ClientboundGadgetRejectPacket packet, ServerPlayer to)
    {
        if (to.Peer != SelfPeer)
            SendPacket(to.Peer, packet, DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundGadgetInstallPacket(ServerboundGadgetInstallPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleInstall(packet, player, this);
    }

    private void OnServerboundGadgetRemovePacket(ServerboundGadgetRemovePacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleRemove(packet, player, this);
    }

    private void OnServerboundGadgetMountPointPacket(ServerboundGadgetMountPointPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleMountPoint(packet, player, this);
    }

    private void OnServerboundGadgetFillHolePacket(ServerboundGadgetFillHolePacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleFillHole(packet, player, this);
    }

    private void OnServerboundGadgetWirePacket(ServerboundGadgetWirePacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleWire(packet, player, this);
    }

    private void OnServerboundGadgetSolderPacket(ServerboundGadgetSolderPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleSolder(packet, player, this);
    }

    private void OnServerboundGadgetSpawnInstallPacket(ServerboundGadgetSpawnInstallPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleSpawnInstall(packet, player, this);
    }

    private void OnServerboundGadgetTextPacket(ServerboundGadgetTextPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleText(packet, player, this);
    }

    private void OnServerboundGadgetSnapPacket(ServerboundGadgetSnapPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleSnap(packet, player, this);
    }

    private void OnServerboundGadgetResyncRequestPacket(ServerboundGadgetResyncRequestPacket packet, ITransportPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            GadgetServer.HandleResync(packet, player, this);
    }
}
