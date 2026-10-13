using Multiplayer.Core.Trains;

namespace Multiplayer.Networking.Packets.Serverbound.Train;

public sealed class ServerboundTrainTopologyCommandPacket
{
    public ulong OperationId { get; set; }
    public uint ExpectedRevision { get; set; }
    public TrainTopologyCommandKind Kind { get; set; }
    public ushort PrimaryCarNetId { get; set; }
    public TrainEnd PrimaryEnd { get; set; }
    public bool HasSecondary { get; set; }
    public ushort SecondaryCarNetId { get; set; }
    public TrainEnd SecondaryEnd { get; set; }
    public bool RequestedValue { get; set; }
    public TrainChainState RequestedChainState { get; set; }
    public bool CascadeHose { get; set; }
    public bool CascadeMultipleUnit { get; set; }
    public bool PlayAudio { get; set; }

    public TrainTopologyCommand ToCore() => new()
    {
        OperationId = OperationId,
        ExpectedRevision = ExpectedRevision,
        Kind = Kind,
        Primary = new TrainEndpointId(PrimaryCarNetId, PrimaryEnd),
        Secondary = HasSecondary
            ? new TrainEndpointId(SecondaryCarNetId, SecondaryEnd)
            : (TrainEndpointId?)null,
        RequestedValue = RequestedValue,
        RequestedChainState = RequestedChainState,
        CascadeHose = CascadeHose,
        CascadeMultipleUnit = CascadeMultipleUnit
    };
}
