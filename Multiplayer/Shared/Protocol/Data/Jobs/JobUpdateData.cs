using DV.ThingTypes;
using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Shared.Protocol.Data.Jobs;

public struct JobUpdateStruct : INetSerializable
{
    public ushort JobNetID;
    public JobState JobState;
    public float StartTime;
    public float FinishTime;
    public ushort ItemNetID;
    public uint ValidationStationId;
    public ItemPositionData ItemPositionData;
    public byte IssuedToPlayerId;

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(JobNetID);
        writer.Put((byte)JobState);
        writer.Put(StartTime);
        writer.Put(FinishTime);
        writer.Put(ItemNetID);
        writer.Put(ValidationStationId);
        ItemPositionData.Serialize(writer,ItemPositionData);
        writer.Put(IssuedToPlayerId);
    }

    public void Deserialize(NetDataReader reader)
    {
        JobNetID = reader.GetUShort();
        JobState = (JobState)reader.GetByte();
        StartTime = reader.GetFloat();
        FinishTime = reader.GetFloat();
        ItemNetID = reader.GetUShort();
        ValidationStationId = reader.GetUInt();
        ItemPositionData = ItemPositionData.Deserialize(reader);
        IssuedToPlayerId = reader.GetByte();
    }
}
