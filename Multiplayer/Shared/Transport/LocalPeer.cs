using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Multiplayer.Shared.Transport;

/// <summary>
///     The host's own player's peer on its server, with no transport: what the server sends it is
///     queued as bytes, in order, and read by the host's local player session at its next poll, a
///     poll later like the loopback connection it replaces (docs/design/host-local-session.md,
///     stage 3). The session reads only presentation packets from it, so nothing the server sends
///     its other players can be applied to the world the server owns.
/// </summary>
public sealed class LocalPeer : ITransportPeer
{
    /// <summary>Not a transport's peer id: no real peer has a negative one.</summary>
    public const int LocalId = -1;

    private readonly Queue<byte[]> received = new();

    public int Id => LocalId;

    public TransportConnectionState ConnectionState { get; private set; } = TransportConnectionState.Connected;

    public void Send(NetDataWriter writer, DeliveryMethod deliveryMethod)
    {
        if (ConnectionState == TransportConnectionState.Connected)
            received.Enqueue(writer.CopyData());
    }

    /// <summary>
    ///     Nobody can disconnect the host's own player from its server (a kick, the server stopping):
    ///     the session ends with the server.
    /// </summary>
    public void Disconnect(NetDataWriter data = null)
    {
        Multiplayer.LogWarning("Ignored a disconnect of the host's own player: it ends with the server");
    }

    /// <summary>The next packet the server sent the local player, if any.</summary>
    public bool TryReceive(out byte[] data)
    {
        if (received.Count > 0)
        {
            data = received.Dequeue();
            return true;
        }
        data = null;
        return false;
    }

    /// <summary>Stops taking packets, when the session stops.</summary>
    public void Close()
    {
        ConnectionState = TransportConnectionState.Disconnected;
        received.Clear();
    }
}
