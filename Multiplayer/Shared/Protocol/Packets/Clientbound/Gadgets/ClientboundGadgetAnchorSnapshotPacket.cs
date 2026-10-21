using Multiplayer.Shared.Protocol.Data.Gadgets;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

/// <summary>
/// Every gadget on one car as the server has it, at the car's revision: the answer to a resync
/// (gadgets.md, "Desync and rollback"). The items' Creates go just before it.
/// </summary>
public class ClientboundGadgetAnchorSnapshotPacket
{
    public GadgetAnchorSnapshotData Anchor { get; set; }
}
