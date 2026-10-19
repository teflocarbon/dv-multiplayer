using LiteNetLib.Utils;

namespace Multiplayer.Shared.Protocol.Data.RPCs;

/// <summary>
/// Base interface for all RPC responses
/// </summary>
public interface IRpcResponse
{
    void Serialize(NetDataWriter writer);
    void Deserialize(NetDataReader reader);
}
