namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundWorldItemCataloguePacket
{
    public bool Accepted { get; set; }
    public int HostItemCount { get; set; }
    public int HostCollisionCount { get; set; }
    public string HostDigest { get; set; }
    public string Reason { get; set; }
}
