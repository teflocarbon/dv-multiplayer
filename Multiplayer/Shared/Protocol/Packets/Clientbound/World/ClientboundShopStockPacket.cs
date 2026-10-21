namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// The shops' stock table as the server has it: per item, how many are bought and how many may exist
// (none while the licence it needs or the garage it unlocks rules it out). A client's game never
// works these out itself: it doesn't run vanilla's load-time rules.
public class ClientboundShopStockPacket
{
    public string[] ItemPrefabNames { get; set; }
    public int[] PurchasedItems { get; set; }
    public int[] AllowedToHaveAmount { get; set; }
}
