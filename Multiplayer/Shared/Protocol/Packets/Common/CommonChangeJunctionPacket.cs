namespace Multiplayer.Shared.Protocol.Packets.Common;

public class CommonChangeJunctionPacket
{
    public ushort NetId { get; set; }
    public byte SelectedBranch { get; set; }
    public byte Mode { get; set; }
}
