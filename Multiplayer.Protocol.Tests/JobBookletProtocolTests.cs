using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class JobBookletProtocolTests
{
    [Test]
    public void PhysicalCopyRoundTripPreservesItemPlayerAndPlacement()
    {
        JobBookletCopyData source = new()
        {
            ItemNetId = 736,
            IssuedToPlayerId = 2,
            Position = new ItemPositionData
            {
                Position = new Vector3(7923.5f, 133.25f, 7336.75f),
                Rotation = new Quaternion(.1f, .2f, .3f, .9f)
            }
        };
        NetDataWriter writer = new();

        JobBookletCopyData.Serialize(writer, source);
        JobBookletCopyData result = JobBookletCopyData.Deserialize(
            new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(result.ItemNetId, Is.EqualTo(736));
            Assert.That(result.IssuedToPlayerId, Is.EqualTo(2));
            Assert.That(result.Position.Position, Is.EqualTo(source.Position.Position));
            Assert.That(result.Position.Rotation, Is.EqualTo(source.Position.Rotation));
        });
    }

    [Test]
    public void IncrementalUpdateRoundTripPreservesIssuedPlayer()
    {
        JobUpdateStruct source = new()
        {
            JobNetID = 19,
            JobState = DV.ThingTypes.JobState.InProgress,
            ItemNetID = 736,
            ValidationStationId = 42,
            IssuedToPlayerId = 2,
            ItemPositionData = new ItemPositionData()
        };
        NetDataWriter writer = new();

        source.Serialize(writer);
        JobUpdateStruct result = new();
        result.Deserialize(new NetDataReader(writer.CopyData()));

        Assert.Multiple(() =>
        {
            Assert.That(result.JobNetID, Is.EqualTo(19));
            Assert.That(result.ItemNetID, Is.EqualTo(736));
            Assert.That(result.ValidationStationId, Is.EqualTo(42));
            Assert.That(result.IssuedToPlayerId, Is.EqualTo(2));
        });
    }
}
