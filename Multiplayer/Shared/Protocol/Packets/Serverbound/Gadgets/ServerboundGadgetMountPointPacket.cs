namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player bolts (drill), tapes (duct tape) or frees (an empty hand, untaping) one of an installed
/// gadget's mount points (gadgets.md, "Requests"), where vanilla's tool would set it. Answered with the
/// change log's <c>MountPoint</c> entry or a rejection.
/// </summary>
public class ServerboundGadgetMountPointPacket
{
    public uint OperationId { get; set; }
    public ushort ItemNetId { get; set; }
    public int Index { get; set; }
    /// <summary>MountPoint.States.</summary>
    public byte State { get; set; }
}
