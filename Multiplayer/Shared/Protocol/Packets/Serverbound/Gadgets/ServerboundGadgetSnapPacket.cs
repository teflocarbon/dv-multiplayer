namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player puts an item on an installed gadget's snap point (a hanger, a shelf) or takes it off (PR 10).
/// Their game shows it at once; the change log's <c>Snapped</c>/<c>Unsnapped</c> confirms it.
/// </summary>
public class ServerboundGadgetSnapPacket
{
    public uint OperationId { get; set; }
    public ushort GadgetItemNetId { get; set; }
    public ushort ItemNetId { get; set; }
    public bool Snap { get; set; }
}
