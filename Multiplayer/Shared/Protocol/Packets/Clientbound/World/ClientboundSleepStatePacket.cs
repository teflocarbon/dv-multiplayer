namespace Multiplayer.Shared.Protocol.Packets.Clientbound.World;

// The shared sleep cooldown: when anyone last woke up and how long they slept. Every player's bed
// uses it, so one player can't sleep again straight after another.
public class ClientboundSleepStatePacket
{
    public double LastWakeTimeOADate { get; set; }
    public int LastSleepDurationSeconds { get; set; }
}
