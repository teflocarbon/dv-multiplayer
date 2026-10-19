namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// The host's count of each shop item bought and not yet restocked; clients show stock from it.
public class ClientboundShopStockPacket
{
    public string[] ItemPrefabNames { get; set; }
    public int[] PurchasedItems { get; set; }
}
