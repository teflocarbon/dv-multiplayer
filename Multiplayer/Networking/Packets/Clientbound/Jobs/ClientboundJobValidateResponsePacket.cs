namespace Multiplayer.Networking.Packets.Clientbound.Jobs;

public class ClientboundJobValidateResponsePacket
{
    public ushort JobNetId { get; set; }
    public bool Invalid { get; set; }
    // The host has no such job: the client's copy is stale and can go. Otherwise only the item
    // offered (an overview or booklet that isn't the job's own) was refused.
    public bool JobMissing { get; set; }
}
