namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// The server refused one of this player's shop requests (ShopRequestKind), and why (ShopRefusal).
public class ClientboundShopRequestResultPacket
{
    public byte ShopIndex { get; set; }
    public byte Request { get; set; }
    public byte Refusal { get; set; }
}
