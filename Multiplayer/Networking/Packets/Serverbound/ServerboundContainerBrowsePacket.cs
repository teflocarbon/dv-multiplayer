namespace Multiplayer.Networking.Packets.Serverbound;

public class ServerboundContainerBrowsePacket
{
    public uint RequestId { get; set; }
    public ushort ShellNetId { get; set; }
    public uint ContainerHandle { get; set; }
    public int Offset { get; set; }
    public byte Count { get; set; }
}
