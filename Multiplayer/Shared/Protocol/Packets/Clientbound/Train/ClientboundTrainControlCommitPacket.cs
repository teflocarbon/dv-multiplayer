namespace Multiplayer.Shared.Protocol.Packets.Clientbound.Train;

public enum TrainControlCommitResult : byte
{
    Accepted = 0,
    StaleRevision = 1,
    AuthorityDenied = 2,
    OutOfRange = 3,
    InvalidValue = 4,
    UnknownControl = 5
}

public sealed class ClientboundTrainControlCommitPacket
{
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    public ulong OperationId { get; set; }
    public TrainControlCommitResult Result { get; set; }
    public uint[] PortIds { get; set; }
    public float[] PortValues { get; set; }
    public uint[] FuseIds { get; set; }
    public bool[] FuseValues { get; set; }
    public bool IncludesHandbrake { get; set; }
    public float HandbrakePosition { get; set; }
    public bool BrakeCylinderReleased { get; set; }
    /// <summary>The host's tick the port values are from: when its simulation produced them, or
    /// when it took a player's. Clients play port values out on this timeline (TrainPortPresenter).</summary>
    public uint Tick { get; set; }
}
