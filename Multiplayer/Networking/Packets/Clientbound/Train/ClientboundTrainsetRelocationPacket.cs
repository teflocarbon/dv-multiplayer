using Multiplayer.Networking.Data.Train;

namespace Multiplayer.Networking.Packets.Clientbound.Train;

/// <summary>
/// Host-committed discontinuous relocation of an entire trainset. The payload is applied as one
/// main-thread transaction so clients never render a half-moved consist.
/// </summary>
public sealed class ClientboundTrainsetRelocationPacket
{
    public string OperationId { get; set; }
    public uint Revision { get; set; }
    public uint HostTick { get; set; }
    public ushort RootNetId { get; set; }
    public uint CommittedHash { get; set; }
    public TrainsetSpawnPart[] Cars { get; set; }
}
