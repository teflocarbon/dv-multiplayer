namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundLostItemsSnapshotPacket
{
    public uint RequestId { get; set; }
    public uint Generation { get; set; }
    public uint[] Handles { get; set; }
    public ushort[] NetIds { get; set; }
    public uint[] Revisions { get; set; }
    public string[] PrefabNames { get; set; }
    public string[] DisplayNames { get; set; }
    public byte[] Reasons { get; set; }
    public long[] LostUtcTicks { get; set; }
}
