namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// The session's shelf seed: every game lays each shop's shelves out from it (ShopShelfLayout).
public class ClientboundShopLayoutPacket
{
    public int Seed { get; set; }
}
