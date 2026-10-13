namespace Multiplayer.Networking.Packets.Serverbound;

// A client saw a world item sink under its terrain (the host may not have terrain loaded there).
public class ServerboundItemBelowMapPacket
{
    public ushort ItemNetId { get; set; }
    public float SurfaceHeight { get; set; } // absolute terrain surface height at the item
}
