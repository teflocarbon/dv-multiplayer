namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

public class ServerboundWorldItemCataloguePacket
{
    public int ItemCount { get; set; }
    public int CollisionCount { get; set; }
    public string Digest { get; set; }
}
