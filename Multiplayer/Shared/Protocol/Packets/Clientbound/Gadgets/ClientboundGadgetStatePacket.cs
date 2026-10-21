namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

/// <summary>
/// An installed gadget's settings as the server has them (gadgets.md, "Gadget state"): sent to every
/// player when they change, whoever or whatever changed them, and to a player whose request was
/// refused. <see cref="PlayerId"/> and <see cref="OperationId"/> name the request the values come
/// from, if one did, so its sender can tell its own change confirmed.
/// </summary>
public class ClientboundGadgetStatePacket
{
    public ushort ItemNetId { get; set; }
    public uint Revision { get; set; }
    public byte PlayerId { get; set; }
    public uint OperationId { get; set; }
    /// <summary>The request named was refused: these are the values it didn't change.</summary>
    public bool Refused { get; set; }
    public string[] Keys { get; set; } = [];
    public float[] Values { get; set; } = [];
}
