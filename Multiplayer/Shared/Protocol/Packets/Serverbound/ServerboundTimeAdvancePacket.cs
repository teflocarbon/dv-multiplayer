namespace Multiplayer.Shared.Protocol.Packets.Serverbound;

public class ServerboundTimeAdvancePacket
{
    public float amountOfTimeToSkipInSeconds { get; set; }
    // Sleeping in a bed (checked against the shared sleep cooldown) rather than fast travel.
    public bool IsSleep { get; set; }
}
