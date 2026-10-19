using LiteNetLib.Utils;
using MPAPI.Interfaces.Packets;
using System.IO;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Shared.Api;

/// <summary>
/// Wrapper for external serializable packets to integrate with LiteNetLib
/// </summary>
/// <typeparam name="T">The packet type</typeparam>
public class ExternalSerializablePacketWrapper<T> : INetSerializable where T : class, ISerializablePacket, new()
{
    const int COMPRESSION_THRESHOLD = 1024;

    public T Packet { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        byte[] data;

        using var memoryStream = new MemoryStream();
        using var binaryWriter = new BinaryWriter(memoryStream);

        Packet.Serialize(binaryWriter);

        data = memoryStream.ToArray();

        bool shouldCompress = memoryStream.Length >= COMPRESSION_THRESHOLD;
        writer.Put(shouldCompress);

        if (shouldCompress)
            data = PacketCompression.Compress(data);

        writer.PutBytesWithLength(data);
    }

    public void Deserialize(NetDataReader reader)
    {
        bool isCompressed = reader.GetBool();
        var data = reader.GetBytesWithLength();

        if (isCompressed)
            data = PacketCompression.Decompress(data);

        using var memoryStream = new MemoryStream(data);
        using var binaryReader = new BinaryReader(memoryStream);

        Packet = new T();
        Packet.Deserialize(binaryReader);
    }
}
