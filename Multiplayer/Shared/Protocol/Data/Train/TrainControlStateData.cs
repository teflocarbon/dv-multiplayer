using LiteNetLib.Utils;
using System;

namespace Multiplayer.Shared.Protocol.Data.Train;

public sealed class TrainControlStateData
{
    public ushort CarNetId { get; set; }
    public uint Revision { get; set; }
    public uint[] PortIds { get; set; }
    public float[] PortValues { get; set; }
    public uint[] FuseIds { get; set; }
    public bool[] FuseValues { get; set; }
    public bool HasHandbrake { get; set; }
    public float HandbrakePosition { get; set; }

    public static void Serialize(NetDataWriter writer, TrainControlStateData data)
    {
        writer.Put(data.CarNetId);
        writer.Put(data.Revision);
        writer.PutArray(data.PortIds ?? System.Array.Empty<uint>());
        writer.PutArray(data.PortValues ?? System.Array.Empty<float>());
        writer.PutArray(data.FuseIds ?? System.Array.Empty<uint>());
        bool[] fuseValues = data.FuseValues ?? System.Array.Empty<bool>();
        writer.Put(fuseValues.Length);
        foreach (bool value in fuseValues)
            writer.Put(value);
        writer.Put(data.HasHandbrake);
        if (data.HasHandbrake)
            writer.Put(data.HandbrakePosition);
    }

    public static TrainControlStateData Deserialize(NetDataReader reader)
    {
        ushort carNetId = reader.GetUShort();
        uint revision = reader.GetUInt();
        uint[] portIds = reader.GetUIntArray();
        float[] portValues = reader.GetFloatArray();
        uint[] fuseIds = reader.GetUIntArray();
        int fuseValueCount = reader.GetInt();
        if (fuseValueCount < 0 || fuseValueCount > 65536)
            throw new InvalidOperationException("Invalid train fuse value count: " + fuseValueCount);
        bool[] fuseValues = new bool[fuseValueCount];
        for (int index = 0; index < fuseValueCount; index++)
            fuseValues[index] = reader.GetBool();
        var result = new TrainControlStateData
        {
            CarNetId = carNetId,
            Revision = revision,
            PortIds = portIds,
            PortValues = portValues,
            FuseIds = fuseIds,
            FuseValues = fuseValues,
            HasHandbrake = reader.GetBool()
        };
        if (result.HasHandbrake)
            result.HandbrakePosition = reader.GetFloat();
        return result;
    }
}
