using Multiplayer.Shared.Protocol.Data.World;

namespace Multiplayer.Shared.Protocol.Packets.Serverbound.Jobs;

public class ServerboundWarehouseMachineControllerRequestPacket
{
    public ushort NetId { get; set; }
    public WarehouseAction WarehouseAction { get; set; }
}
