using DV.Utils;
using Multiplayer.Client.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

namespace Multiplayer.Client.Session;

// NetworkClient's gadget handlers (see docs/design/gadgets.md).
public partial class NetworkClient
{
    // Set when the join's gadget snapshot has been applied (SyncWorldState waits for it).
    private bool gadgetJoinApplied;

    private void OnClientboundGadgetJoinSnapshotPacket(ClientboundGadgetJoinSnapshotPacket packet)
    {
        Log($"Received installed gadgets on {packet.Anchors?.Length ?? 0} cars");
        SingletonBehaviour<CoroutineManager>.Instance.Run(GadgetJoinApplier.Apply(packet, () => gadgetJoinApplied = true));
    }
}
