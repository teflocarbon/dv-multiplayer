using System;
using System.Linq;
using System.Reflection;
using Multiplayer.Networking.Managers.Client;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Common;
using NUnit.Framework;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class HostOwnClientPacketTests
{
    [Test]
    public void EveryClientboundPacketSaysWhetherTheHostsOwnClientAppliesIt()
    {
        Type[] clientbound = LoadableTypes().Where(IsClientboundPacket).ToArray();

        // Every packet loads here (some of the mod's other types need game assemblies the tests lack).
        Assert.That(clientbound.Length, Is.GreaterThanOrEqualTo(HostOwnClient.IgnoredPacketTypes.Concat(
            HostOwnClient.AppliedPacketTypes).Count(type => type.Name.StartsWith("Clientbound"))));
        Assert.That(clientbound.Where(type => !HostOwnClient.Classifies(type)).Select(type => type.Name), Is.Empty,
            "add each to HostOwnClient: ignored if it carries the host's state for players to follow");
    }

    private static bool IsClientboundPacket(Type type)
    {
        try
        {
            return type.IsClass && !type.IsAbstract && type.Name.StartsWith("Clientbound") &&
                   type.Name.EndsWith("Packet") &&
                   type.Namespace?.StartsWith("Multiplayer.Networking.Packets.Clientbound") == true;
        }
        catch (Exception)
        {
            return false; // a nested type of something that needs the game's assemblies
        }
    }

    private static Type[] LoadableTypes()
    {
        try
        {
            return typeof(HostOwnClient).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(type => type != null).ToArray();
        }
    }

    [Test]
    public void NoPacketIsBothIgnoredAndAppliedOnTheHost() =>
        Assert.That(HostOwnClient.IgnoredPacketTypes.Intersect(HostOwnClient.AppliedPacketTypes), Is.Empty);

    [Test]
    public void TheHostsOwnClientNeverFollowsItsRollingStock()
    {
        Assert.Multiple(() =>
        {
            foreach (Type type in new[]
                     {
                         typeof(ClientboundTrainTopologySnapshotPacket), typeof(ClientboundSpawnTrainSetPacket),
                         typeof(ClientboundDestroyTrainCarPacket), typeof(ClientboundTrainsetPhysicsPacket),
                         typeof(ClientboundBrakeStateUpdatePacket), typeof(ClientboundTrainControlCommitPacket),
                         typeof(ClientboundTrainsetRelocationPacket)
                     })
                Assert.That(HostOwnClient.Ignores(type), Is.True, type.Name);
        });
    }

    [Test]
    public void TheHostsOwnClientStillTakesRelayedJunctionChanges() =>
        // The server relays them without switching its own junctions; this client does that.
        Assert.That(HostOwnClient.Ignores(typeof(CommonChangeJunctionPacket)), Is.False);
}
