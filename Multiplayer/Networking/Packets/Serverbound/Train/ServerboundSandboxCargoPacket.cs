namespace Multiplayer.Networking.Packets.Serverbound.Train;

// A client used the comms radio's sandbox cargo loader on a car: fill it with a cargo type, or
// empty it (CargoTypeNetId 0). The host does the loading, so every player sees it.
public class ServerboundSandboxCargoPacket
{
    public ushort CarNetId { get; set; }
    public uint CargoTypeNetId { get; set; }
}
