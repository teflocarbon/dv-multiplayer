using LiteNetLib.Utils;

namespace Multiplayer.Networking.Data.Train;

public sealed class TrainControlRevisionData
{
    public ushort CarNetId { get; set; }
    public uint Revision { get; set; }

    public static void Serialize(NetDataWriter writer, TrainControlRevisionData data)
    {
        writer.Put(data.CarNetId);
        writer.Put(data.Revision);
    }

    public static TrainControlRevisionData Deserialize(NetDataReader reader) => new()
    {
        CarNetId = reader.GetUShort(),
        Revision = reader.GetUInt()
    };
}
