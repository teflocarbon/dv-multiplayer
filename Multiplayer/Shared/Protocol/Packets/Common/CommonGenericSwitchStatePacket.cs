
namespace Multiplayer.Shared.Protocol.Packets.Common;

public class CommonGenericSwitchStatePacket
{
    public uint NetId { get; set; }
    public bool IsOn { get; set; }
}
