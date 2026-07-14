using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Common;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests;

[TestFixture]
public sealed class ItemPacketFixtureTests
{
    [TestCase(ItemUpdateData.ItemUpdateType.ObjectState, false)]
    [TestCase(ItemUpdateData.ItemUpdateType.ItemPosition, false)]
    [TestCase(ItemUpdateData.ItemUpdateType.ItemState, true)]
    [TestCase(ItemUpdateData.ItemUpdateType.FullSync, true)]
    [TestCase(ItemUpdateData.ItemUpdateType.Create, true)]
    [TestCase(ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.ObjectState, true)]
    public void PlacementDetectionDoesNotTreatObjectStateAsFullSync(
        ItemUpdateData.ItemUpdateType updateType, bool expected)
    {
        Assert.That(ItemUpdateData.IncludesItemState(updateType), Is.EqualTo(expected));
    }

    [Test]
    public void ExistingProjectorFixtureRunsUnderNUnit()
    {
        Assert.DoesNotThrow(DebugPacketProjectorSelfTests.Run);
    }

    [TestCase(ItemState.Dropped)]
    [TestCase(ItemState.Thrown)]
    [TestCase(ItemState.InHand)]
    [TestCase(ItemState.InInventory)]
    [TestCase(ItemState.Attached)]
    public void ItemStateRoundTripsOnlyItsWireSemanticFields(ItemState state)
    {
        ItemUpdateData source = Fixture(ItemUpdateData.ItemUpdateType.ItemState, state);
        ItemUpdateData result = RoundTrip(source);

        AssertCommon(source, result);
        Assert.That(result.ItemState, Is.EqualTo(state));
        switch (state)
        {
            case ItemState.Dropped:
                AssertWorldTransform(result);
                Assert.That(result.ThrowDirection, Is.EqualTo(Vector3.zero));
                break;
            case ItemState.Thrown:
                AssertWorldTransform(result);
                AssertVector(result.ThrowDirection, source.ThrowDirection);
                break;
            case ItemState.InHand:
            case ItemState.InInventory:
                Assert.That(result.PlayerId, Is.EqualTo(source.PlayerId));
                Assert.That(result.ItemPosition, Is.EqualTo(Vector3.zero));
                break;
            case ItemState.Attached:
                Assert.Multiple(() => {
                    Assert.That(result.CarNetId, Is.EqualTo(source.CarNetId));
                    Assert.That(result.AttachedFront, Is.EqualTo(source.AttachedFront));
                });
                break;
        }
        Assert.Multiple(() => {
            Assert.That(result.PrefabName, Is.Null);
            Assert.That(result.States, Is.Null);
        });
    }

    [Test]
    public void CreateRoundTripsPrefabPlacementAndAllTrackedTypes()
    {
        ItemUpdateData result = RoundTrip(Fixture(ItemUpdateData.ItemUpdateType.Create, ItemState.Dropped));
        Assert.Multiple(() => {
            Assert.That(result.PrefabName, Is.EqualTo("Cup2"));
            Assert.That(result.States["bool"], Is.EqualTo(true));
            Assert.That(result.States["int"], Is.EqualTo(-2));
            Assert.That(result.States["uint"], Is.EqualTo(7u));
            Assert.That(result.States["float"], Is.EqualTo(1.25f));
            Assert.That(result.States["string"], Is.EqualTo("state"));
        });
        AssertWorldTransform(result);
    }

    [Test]
    public void DestroyRoundTripsEnvelopeOnly()
    {
        ItemUpdateData result = RoundTrip(Fixture(ItemUpdateData.ItemUpdateType.Destroy, ItemState.Thrown));
        Assert.Multiple(() => {
            Assert.That(result.UpdateType, Is.EqualTo(ItemUpdateData.ItemUpdateType.Destroy));
            Assert.That(result.ItemNetId, Is.EqualTo(78));
            Assert.That(result.AuthorityRevision, Is.EqualTo(42));
            Assert.That(result.ItemState, Is.EqualTo(default(ItemState)));
            Assert.That(result.PrefabName, Is.Null);
            Assert.That(result.States, Is.Null);
        });
    }

    [TestCase(ItemUpdateData.ItemUpdateType.ObjectState)]
    [TestCase(ItemUpdateData.ItemUpdateType.FullSync)]
    public void StateBearingUpdatesRoundTripTrackedState(ItemUpdateData.ItemUpdateType updateType)
    {
        ItemUpdateData result = RoundTrip(Fixture(updateType, ItemState.Thrown));
        Assert.That(result.States, Has.Count.EqualTo(5));
        if (updateType == ItemUpdateData.ItemUpdateType.FullSync)
        {
            AssertWorldTransform(result);
            AssertVector(result.ThrowDirection, new Vector3(.839f, -.528f, -.132f));
        }
        else
        {
            Assert.That(result.ItemPosition, Is.EqualTo(Vector3.zero));
            Assert.That(result.ThrowDirection, Is.EqualTo(Vector3.zero));
        }
    }

    [Test]
    public void DecodedProjectionNeverContainsRecursiveUnityProperties()
    {
        DebugPacketProjection projection = DebugPacketProjectorRegistry.Project(
            new CommonItemUpdatePacket { ItemData = Fixture(ItemUpdateData.ItemUpdateType.FullSync, ItemState.Thrown) }, 2);
        string json = DebugJson.Serialize(projection.Wire);
        foreach (string forbidden in new[] { "normalized", "sqrMagnitude", "eulerAngles", "maximum depth" })
            Assert.That(json, Does.Not.Contain(forbidden).IgnoreCase);
    }

    [Test]
    public void UnsupportedTrackedTypeFailsBeforeProducingAmbiguousWireData()
    {
        ItemUpdateData data = Fixture(ItemUpdateData.ItemUpdateType.ObjectState, ItemState.Dropped);
        data.States = new Dictionary<string, object> { ["unsupported"] = new object() };
        Assert.Throws<NotSupportedException>(() => ItemUpdateData.Serialize(new NetDataWriter(), data));
    }

    private static ItemUpdateData RoundTrip(ItemUpdateData source)
    {
        var writer = new NetDataWriter();
        ItemUpdateData.Serialize(writer, source);
        var reader = new NetDataReader(writer.Data, 0, writer.Length);
        ItemUpdateData result = ItemUpdateData.Deserialize(reader);
        Assert.That(reader.AvailableBytes, Is.Zero, "fixture must consume the complete payload");
        return result;
    }

    private static ItemUpdateData Fixture(ItemUpdateData.ItemUpdateType updateType, ItemState state) => new()
    {
        UpdateType = updateType,
        ItemNetId = 78,
        AuthorityRevision = 42,
        PersistentOwnerPlayerId = 2,
        InventoryClaimPlayerId = 2,
        InventoryClaimSlot = 4,
        InventoryClaimFlags = ItemInventoryClaimFlags.Reserved,
        TransitionReason = ItemTransitionReason.ClientState,
        PrefabName = "Cup2",
        ItemState = state,
        ItemPosition = new Vector3(9469.9f, 120.599f, 13616.892f),
        ItemRotation = new Quaternion(.175f, .732f, -.211f, .624f),
        ThrowDirection = new Vector3(.839f, -.528f, -.132f),
        PlayerId = 2,
        CarNetId = 22,
        AttachedFront = true,
        States = new Dictionary<string, object> {
            ["bool"] = true, ["int"] = -2, ["uint"] = 7u, ["float"] = 1.25f, ["string"] = "state"
        }
    };

    private static void AssertCommon(ItemUpdateData source, ItemUpdateData result) => Assert.Multiple(() => {
        Assert.That(result.UpdateType, Is.EqualTo(source.UpdateType));
        Assert.That(result.ItemNetId, Is.EqualTo(source.ItemNetId));
        Assert.That(result.AuthorityRevision, Is.EqualTo(source.AuthorityRevision));
        Assert.That(result.PersistentOwnerPlayerId, Is.EqualTo(source.PersistentOwnerPlayerId));
        Assert.That(result.InventoryClaimPlayerId, Is.EqualTo(source.InventoryClaimPlayerId));
        Assert.That(result.InventoryClaimSlot, Is.EqualTo(source.InventoryClaimSlot));
        Assert.That(result.InventoryClaimFlags, Is.EqualTo(source.InventoryClaimFlags));
        Assert.That(result.TransitionReason, Is.EqualTo(source.TransitionReason));
    });

    private static void AssertWorldTransform(ItemUpdateData result)
    {
        AssertVector(result.ItemPosition, new Vector3(9469.9f, 120.599f, 13616.892f));
        Assert.Multiple(() => {
            Assert.That(result.ItemRotation.x, Is.EqualTo(.175f).Within(.0001f));
            Assert.That(result.ItemRotation.y, Is.EqualTo(.732f).Within(.0001f));
            Assert.That(result.ItemRotation.z, Is.EqualTo(-.211f).Within(.0001f));
            Assert.That(result.ItemRotation.w, Is.EqualTo(.624f).Within(.0001f));
        });
    }

    private static void AssertVector(Vector3 actual, Vector3 expected) => Assert.Multiple(() => {
        Assert.That(actual.x, Is.EqualTo(expected.x).Within(.0001f));
        Assert.That(actual.y, Is.EqualTo(expected.y).Within(.0001f));
        Assert.That(actual.z, Is.EqualTo(expected.z).Within(.0001f));
    });
}
