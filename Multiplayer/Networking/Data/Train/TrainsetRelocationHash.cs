using System;

namespace Multiplayer.Networking.Data.Train;

public static class TrainsetRelocationHash
{
    public static uint Compute(TrainsetSpawnPart[] cars)
    {
        unchecked
        {
            uint hash = 2166136261;
            if (cars == null) return hash;
            foreach (TrainsetSpawnPart car in cars)
            {
                Add(ref hash, car.NetId);
                Add(ref hash, FloatBits(car.Position.x));
                Add(ref hash, FloatBits(car.Position.y));
                Add(ref hash, FloatBits(car.Position.z));
                Add(ref hash, FloatBits(car.Rotation.x));
                Add(ref hash, FloatBits(car.Rotation.y));
                Add(ref hash, FloatBits(car.Rotation.z));
                Add(ref hash, FloatBits(car.Rotation.w));
                Add(ref hash, car.Bogie1.TrackNetId);
                Add(ref hash, car.Bogie2.TrackNetId);
                AddDouble(ref hash, car.Bogie1.PositionAlongTrack);
                AddDouble(ref hash, car.Bogie2.PositionAlongTrack);
            }
            return hash;
        }
    }

    private static uint FloatBits(float value) =>
        BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);

    private static void Add(ref uint hash, uint value) => hash = (hash ^ value) * 16777619;

    private static void AddDouble(ref uint hash, double value)
    {
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        Add(ref hash, (uint)bits);
        Add(ref hash, (uint)(bits >> 32));
    }
}
