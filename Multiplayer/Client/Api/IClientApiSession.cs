using MPAPI.Interfaces.Packets;
using MPAPI.Types;

namespace Multiplayer.Client.Api;

/// <summary>
///     What MPAPI's client API (<see cref="ClientAPIProvider"/>) needs from the player at this
///     keyboard: a remote client's <c>NetworkClient</c>, or a host's <c>LocalPlayerSession</c>
///     (docs/design/host-local-session.md, stage 3), so mods see the same client API on both.
/// </summary>
public interface IClientApiSession
{
    byte PlayerId { get; }
    int Ping { get; }
    bool IsRunning { get; }

    void RegisterExternalPacket<T>(ClientPacketHandler<T> handler) where T : class, IPacket, new();
    void RegisterExternalSerializablePacket<T>(ClientPacketHandler<T> handler) where T : class, ISerializablePacket, new();
    void SendExternalPacketToServer<T>(T packet, bool reliable) where T : class, IPacket, new();
    void SendExternalSerializablePacketToServer<T>(T packet, bool reliable) where T : class, ISerializablePacket, new();

    /// <summary>A mod holds the join until it's ready (a remote client's loading waits for it).</summary>
    void RegisterReadyBlock(string modName);
    void CancelReadyBlock(string modName);
}
