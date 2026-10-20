using LiteNetLib.Utils;
using MPAPI.Interfaces.Packets;
using MPAPI.Types;
using Multiplayer.Shared.Api;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Transport;

namespace Multiplayer.Client.Session;

// MPAPI's client API on the host (IClientApiSession): mods' packets as they have them on a remote
// client. The server's sends reach their handlers through the LocalPeer, serialised, so a mod gets
// a fresh packet as from the network; theirs reach the server's handlers the same way, as from the
// host's player.
public partial class LocalPlayerSession
{
    private readonly NetDataWriter externalWriter = new();

    public bool IsRunning => Peer.ConnectionState == TransportConnectionState.Connected;

    public void RegisterExternalPacket<T>(ClientPacketHandler<T> handler) where T : class, IPacket, new() =>
        processor.SubscribeReusable<T>(packet => handler(packet));

    public void RegisterExternalSerializablePacket<T>(ClientPacketHandler<T> handler) where T : class, ISerializablePacket, new() =>
        processor.SubscribeNetSerializable<ExternalSerializablePacketWrapper<T>>(wrapper => handler(wrapper.Packet),
            () => new ExternalSerializablePacketWrapper<T>());

    public void SendExternalPacketToServer<T>(T packet, bool reliable) where T : class, IPacket, new()
    {
        externalWriter.Reset();
        processor.Write(externalWriter, packet);
        SendExternalToServer(externalWriter);
    }

    public void SendExternalSerializablePacketToServer<T>(T packet, bool reliable) where T : class, ISerializablePacket, new()
    {
        ExternalSerializablePacketWrapper<T> wrapper = new() { Packet = packet };
        externalWriter.Reset();
        processor.WriteNetSerializable(externalWriter, ref wrapper);
        SendExternalToServer(externalWriter);
    }

    private static void SendExternalToServer(NetDataWriter writer) =>
        NetworkLifecycle.Instance.LocalServer?.ReceiveFromLocalPlayer(writer.CopyData());

    // The host's own loading doesn't wait for mods: it has its world already.
    public void RegisterReadyBlock(string modName) =>
        Multiplayer.Log($"[LocalPlayer] Ready block from {modName}: the host's player doesn't wait for one");

    public void CancelReadyBlock(string modName) { }
}
