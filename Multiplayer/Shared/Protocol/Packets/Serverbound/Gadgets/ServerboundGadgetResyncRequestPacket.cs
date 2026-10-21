namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A client lost track of a car's gadgets (a gap in its revisions, an entry it couldn't apply): it asks
/// for the car's snapshot (gadgets.md, "Desync and rollback").
/// </summary>
public class ServerboundGadgetResyncRequestPacket
{
    public ushort CarNetId { get; set; }
    public uint AppliedRevision { get; set; }
}
