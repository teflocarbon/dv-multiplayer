using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data.RPCs;

namespace Multiplayer.Shared.Protocol.Packets.Clientbound;

/// <summary>
/// Generic packet for sending RPC responses from server to client
/// </summary>
public class ClientboundRpcResponsePacket : INetSerializable
{
    public uint TicketId { get; set; }
    public uint ResponseType { get; set; }
    public IRpcResponse Response { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(TicketId);

        // Find the response type hash/id for this packet type
        ResponseType = RpcResponseTypes.GetResponseTypeHash(Response);

        writer.Put(ResponseType);
        Response?.Serialize(writer);
    }

    public void Deserialize(NetDataReader reader)
    {
        TicketId = reader.GetUInt();
        ResponseType = reader.GetUInt();

        // Get a new instance of the correct response type based on the hash/id, then deserialise it
        Response = RpcResponseTypes.CreateResponseInstance(ResponseType);
        Response.Deserialize(reader);
    }
}
