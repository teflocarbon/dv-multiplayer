using LiteNetLib.Utils;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class TrackGraphPacketTests
{
    [Test]
    public void LoadStateUpdate_RoundTripsTheTrackGraph()
    {
        ServerboundLoadStateUpdatePacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ServerboundLoadStateUpdatePacket>(packet => result = new ServerboundLoadStateUpdatePacket
        {
            LoadState = packet.LoadState,
            TrackGraphHash = packet.TrackGraphHash,
            TrackCount = packet.TrackCount,
            JunctionCount = packet.JunctionCount,
            TurntableCount = packet.TurntableCount
        });
        NetDataWriter writer = new();
        processor.Write(writer, new ServerboundLoadStateUpdatePacket
        {
            LoadState = PlayerLoadingState.ReadyForWorldState,
            TrackGraphHash = 0xF00DCAFE12345678UL,
            TrackCount = 2310,
            JunctionCount = 412,
            TurntableCount = 9
        });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));

        Assert.That(result, Is.Not.Null);
        Assert.That(result.LoadState, Is.EqualTo(PlayerLoadingState.ReadyForWorldState));
        Assert.That(result.TrackGraphHash, Is.EqualTo(0xF00DCAFE12345678UL));
        Assert.That((result.TrackCount, result.JunctionCount, result.TurntableCount), Is.EqualTo(((ushort)2310, (ushort)412, (ushort)9)));
    }

    [Test]
    public void Disconnect_RoundTripsTheReasonAndDefaultsToEmpty()
    {
        ClientboundDisconnectPacket result = null;
        NetPacketProcessor processor = new();
        processor.SubscribeReusable<ClientboundDisconnectPacket>(packet => result = new ClientboundDisconnectPacket
        {
            Kicked = packet.Kicked,
            Reason = packet.Reason
        });

        NetDataWriter writer = new();
        processor.Write(writer, new ClientboundDisconnectPacket { Kicked = true, Reason = "Your railway doesn't match" });
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result.Kicked, Is.True);
        Assert.That(result.Reason, Is.EqualTo("Your railway doesn't match"));

        writer = new NetDataWriter();
        processor.Write(writer, new ClientboundDisconnectPacket());
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result.Kicked, Is.False);
        Assert.That(result.Reason, Is.Empty);
    }
}
