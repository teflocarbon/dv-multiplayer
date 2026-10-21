using Multiplayer.Shared.Protocol.Data.Gadgets;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

/// <summary>
/// One car's gadget changes from one server tick, in revision order (gadgets.md, "The change log"),
/// after the item Creates they need.
/// </summary>
public class ClientboundGadgetChangesPacket
{
    public ushort CarNetId { get; set; }
    public GadgetChangeData[] Entries { get; set; } = [];
}
