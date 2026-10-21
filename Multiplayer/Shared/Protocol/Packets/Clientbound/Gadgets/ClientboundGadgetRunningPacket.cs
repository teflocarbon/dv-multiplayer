namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

/// <summary>
/// An installed gadget's running state as the server has it (gadgets.md, "Gadget state"): sent when
/// a step changes or a count resets, once a second while a count runs, and in answer to a command.
/// <see cref="PlayerId"/> and <see cref="OperationId"/> name the command it answers, if it does.
/// </summary>
public class ClientboundGadgetRunningPacket
{
    public ushort ItemNetId { get; set; }
    public byte PlayerId { get; set; }
    public uint OperationId { get; set; }
    /// <summary>The command named was refused: this is the state it didn't change.</summary>
    public bool Refused { get; set; }
    public string[] Keys { get; set; } = [];
    public float[] Values { get; set; } = [];
}
