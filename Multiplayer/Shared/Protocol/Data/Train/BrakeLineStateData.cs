using LiteNetLib.Utils;
using Multiplayer.Core.Trains;

namespace Multiplayer.Shared.Protocol.Data.Train;

public sealed class BrakeLineStateData
{
    public ushort AnchorCarNetId { get; set; }
    public TrainEnd AnchorEnd { get; set; }
    public uint MemberDigest { get; set; }
    public float PipePressure { get; set; }

    public static void Serialize(NetDataWriter writer, BrakeLineStateData data)
    {
        writer.Put(data.AnchorCarNetId);
        writer.Put((byte)data.AnchorEnd);
        writer.Put(data.MemberDigest);
        writer.Put(data.PipePressure);
    }

    public static BrakeLineStateData Deserialize(NetDataReader reader) => new()
    {
        AnchorCarNetId = reader.GetUShort(),
        AnchorEnd = (TrainEnd)reader.GetByte(),
        MemberDigest = reader.GetUInt(),
        PipePressure = reader.GetFloat()
    };
}
