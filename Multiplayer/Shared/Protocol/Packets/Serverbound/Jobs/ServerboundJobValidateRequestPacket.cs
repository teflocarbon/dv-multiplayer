using Multiplayer.Shared.Protocol.Data.Jobs;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;

public class ServerboundJobValidateRequestPacket
{
    public ushort JobNetId { get; set; }
    public uint StationNetId { get; set; }
    public ushort ItemNetId { get; set; }
    public ValidationType validationType { get; set; }
}
