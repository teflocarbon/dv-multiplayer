namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;

/// <summary>Another player's gadget tool is working, or stopped (PR 11), for players within hearing range.</summary>
public class ClientboundGadgetToolUsePacket
{
    public byte PlayerId { get; set; }
    public ushort ToolItemNetId { get; set; }
    public bool Working { get; set; }
}
