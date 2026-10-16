namespace Multiplayer.Networking.Packets.Clientbound.Train;

// A car's cargo fire changed on the host (CargoFireSync): burning, about to explode, or neither.
// Clients hold their copy of that cargo reaction to it.
public class ClientboundCargoFirePacket
{
    public ushort CarNetId { get; set; }
    public byte Index { get; set; }  // which of the car's cargo reactions
    public byte State { get; set; }  // CargoFireSync.FireState
}
