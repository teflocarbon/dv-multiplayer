namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// The player's gadget tool (drill, soldering iron, tape, wiring tool, hole filler, hammer) started or
/// stopped working (gadgets.md, "Tool use other players see", PR 11); sent again every second while it
/// works.
/// </summary>
public class ServerboundGadgetToolUsePacket
{
    public ushort ToolItemNetId { get; set; }
    public bool Working { get; set; }
}
