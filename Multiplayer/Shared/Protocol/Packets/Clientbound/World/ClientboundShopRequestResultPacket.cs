namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// The server refused one of this player's shop requests (ShopRequestKind), and why (ShopRefusal).
public class ClientboundShopRequestResultPacket
{
    // This player's request counter: the answer names it, and its prediction leaves when it comes.
    public uint RequestId { get; set; }
    public byte ShopIndex { get; set; }
    public byte Request { get; set; }
    public byte Refusal { get; set; }
}
