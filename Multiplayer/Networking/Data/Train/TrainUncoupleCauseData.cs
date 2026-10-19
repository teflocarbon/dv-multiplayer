using LiteNetLib.Utils;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;

namespace Multiplayer.Networking.Data.Train;

/// <summary>Why the host took a coupling apart: one end of it and the cause.</summary>
public sealed class TrainUncoupleCauseData
{
    public ushort CarNetId { get; set; }
    public TrainEnd End { get; set; }
    public ChangeCause<UncoupleReason> Cause { get; set; }

    public static void Serialize(NetDataWriter writer, TrainUncoupleCauseData data)
    {
        writer.Put(data.CarNetId);
        writer.Put((byte)data.End);
        ChangeCauseData.Serialize(writer, data.Cause);
    }

    public static TrainUncoupleCauseData Deserialize(NetDataReader reader) => new()
    {
        CarNetId = reader.GetUShort(),
        End = (TrainEnd)reader.GetByte(),
        Cause = ChangeCauseData.Deserialize<UncoupleReason>(reader)
    };
}
