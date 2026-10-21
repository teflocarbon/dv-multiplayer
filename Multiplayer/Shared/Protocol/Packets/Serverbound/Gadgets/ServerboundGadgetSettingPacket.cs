namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;

/// <summary>
/// A player changed an installed gadget's settings (a knob, a switch, the HUD): the new values by
/// vanilla save key (gadgets.md, "Gadget state"). The player's own game already shows them; the
/// server's state broadcast confirms them or, refused, puts them back.
/// </summary>
public class ServerboundGadgetSettingPacket
{
    public ushort ItemNetId { get; set; }
    public uint OperationId { get; set; }
    public string[] Keys { get; set; } = [];
    public float[] Values { get; set; } = [];
}
