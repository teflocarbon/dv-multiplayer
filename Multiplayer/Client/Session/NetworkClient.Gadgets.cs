using DV.Utils;
using LiteNetLib;
using Multiplayer.Client.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

namespace Multiplayer.Client.Session;

// NetworkClient's gadget handlers and sends (see docs/design/gadgets.md).
public partial class NetworkClient
{
    // Set when the join's gadget snapshot has been applied (SyncWorldState waits for it).
    private bool gadgetJoinApplied;

    private void OnClientboundGadgetJoinSnapshotPacket(ClientboundGadgetJoinSnapshotPacket packet)
    {
        Log($"Received installed gadgets on {packet.Anchors?.Length ?? 0} cars");
        SingletonBehaviour<CoroutineManager>.Instance.Run(GadgetJoinApplier.Apply(packet, () => gadgetJoinApplied = true));
    }

    private void OnClientboundGadgetStatePacket(ClientboundGadgetStatePacket packet) => GadgetStateClient.Receive(packet);

    internal void SendGadgetSetting(ServerboundGadgetSettingPacket packet) =>
        SendPacketToServer(packet, DeliveryMethod.ReliableOrdered);

    private void OnClientboundGadgetRunningPacket(ClientboundGadgetRunningPacket packet) => GadgetRunningClient.Receive(packet);

    internal void SendGadgetCommand(ServerboundGadgetCommandPacket packet) =>
        SendPacketToServer(packet, DeliveryMethod.ReliableOrdered);

    internal void SendGadgetPanel(ServerboundGadgetPanelPacket packet) =>
        SendPacketToServer(packet, DeliveryMethod.ReliableOrdered);
}
