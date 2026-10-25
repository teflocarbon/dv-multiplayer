namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

public class ServerboundWorldItemCataloguePacket
{
    /// <summary>Every key the client's scenes have, collided ones too.</summary>
    public int ItemCount { get; set; }
    public int CollisionCount { get; set; }
    public string Digest { get; set; }
    /// <summary>The keys two of the client's objects make (OI-SYNC-08): refused alone.</summary>
    public string[] CollisionKeys { get; set; }
}
