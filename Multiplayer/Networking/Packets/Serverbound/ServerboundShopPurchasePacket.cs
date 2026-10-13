namespace Multiplayer.Networking.Packets.Serverbound;

// A client bought items at a shop. The host spawns them, so they exist for everyone, and pays for
// them out of what the client has put into shop registers (ServerboundShopPaymentPacket).
public class ServerboundShopPurchasePacket
{
    public int ShopIndex { get; set; }
    public string ItemPrefabName { get; set; }
    public int Amount { get; set; }
}
