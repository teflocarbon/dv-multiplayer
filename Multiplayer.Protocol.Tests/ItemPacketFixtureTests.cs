using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
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

    // A flashlight's battery ticking on the server's copy must not count as moving it (playtest 3).
    [TestCase(ItemUpdateData.ItemUpdateType.ObjectState, false)]
    [TestCase(ItemUpdateData.ItemUpdateType.None, false)]
    [TestCase(ItemUpdateData.ItemUpdateType.ItemPosition, true)]
    [TestCase(ItemUpdateData.ItemUpdateType.ItemPosition | ItemUpdateData.ItemUpdateType.ObjectState, true)]
    [TestCase(ItemUpdateData.ItemUpdateType.ItemState, true)]
    [TestCase(ItemUpdateData.ItemUpdateType.FullSync, true)]
    [TestCase(ItemUpdateData.ItemUpdateType.Create, true)]
    public void ObjectStateAloneDoesNotChangePlacement(ItemUpdateData.ItemUpdateType updateType, bool expected)
    {
        Assert.That(ItemUpdateData.ChangesPlacement(updateType), Is.EqualTo(expected));
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
                Assert.That(result.PlayerHand, Is.EqualTo(PlayerHand.Left));
                Assert.That(result.PlayerId, Is.EqualTo(source.PlayerId));
                Assert.That(result.ItemPosition, Is.EqualTo(Vector3.zero));
                break;
            case ItemState.InInventory:
                Assert.That(result.PlayerId, Is.EqualTo(source.PlayerId));
                Assert.That(result.PlayerHand, Is.EqualTo(PlayerHand.None));
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
            Assert.That(result.AuthoredItemKey, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(result.BelongsToPlayer, Is.True);
            Assert.That(result.States["bool"], Is.EqualTo(true));
            Assert.That(result.States["int"], Is.EqualTo(-2));
            Assert.That(result.States["uint"], Is.EqualTo(7u));
            Assert.That(result.States["float"], Is.EqualTo(1.25f));
            Assert.That(result.States["string"], Is.EqualTo("state"));
        });
        AssertWorldTransform(result);
    }

    [TestCase(ItemWorldParentKind.TrainInterior)]
    [TestCase(ItemWorldParentKind.StaticParent)]
    public void WorldParentRoundTripsCompactAnchorAndLocalPose(ItemWorldParentKind kind)
    {
        ItemUpdateData source = Fixture(ItemUpdateData.ItemUpdateType.ItemState, ItemState.Dropped);
        source.WorldParentKind = kind;
        source.WorldParentNetId = 91;
        source.WorldParentKey = "fedcba9876543210fedcba9876543210";
        source.ParentLocalPosition = new Vector3(1.5f, .25f, -3f);
        source.ParentLocalRotation = new Quaternion(.182574f, .365148f, .547723f, .730297f);

        ItemUpdateData result = RoundTrip(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.WorldParentKind, Is.EqualTo(kind));
            Assert.That(result.WorldParentNetId,
                Is.EqualTo(kind == ItemWorldParentKind.TrainInterior ? 91 : 0));
            Assert.That(result.WorldParentKey,
                Is.EqualTo(kind == ItemWorldParentKind.StaticParent
                    ? "fedcba9876543210fedcba9876543210"
                    : null));
        });
        AssertVector(result.ParentLocalPosition, source.ParentLocalPosition);
        Assert.Multiple(() =>
        {
            Assert.That(result.ParentLocalRotation.x, Is.EqualTo(source.ParentLocalRotation.x).Within(.0001f));
            Assert.That(result.ParentLocalRotation.y, Is.EqualTo(source.ParentLocalRotation.y).Within(.0001f));
            Assert.That(result.ParentLocalRotation.z, Is.EqualTo(source.ParentLocalRotation.z).Within(.0001f));
            Assert.That(result.ParentLocalRotation.w, Is.EqualTo(source.ParentLocalRotation.w).Within(.0001f));
        });
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

    [Test]
    public void DestroyRoundTripsItsRemovalCause()
    {
        ItemUpdateData source = Fixture(ItemUpdateData.ItemUpdateType.Destroy, ItemState.Dropped);
        source.RemovalCause = new ChangeCause<ItemRemovalReason>(ItemRemovalReason.CollectedToLostAndFound,
            0, null, 9001, "OwnerDistance, owner Bentgear29");
        source.MoveCause = new ChangeCause<ItemMoveReason>(ItemMoveReason.Dropped); // not on a Destroy

        ItemUpdateData result = RoundTrip(source);

        Assert.That(result.RemovalCause.HasValue, Is.True);
        Assert.Multiple(() => {
            Assert.That(result.RemovalCause.Value.Reason, Is.EqualTo(ItemRemovalReason.CollectedToLostAndFound));
            Assert.That(result.RemovalCause.Value.Tick, Is.EqualTo(9001));
            Assert.That(result.RemovalCause.Value.Detail, Is.EqualTo("OwnerDistance, owner Bentgear29"));
            Assert.That(result.MoveCause, Is.Null);
        });
    }

    [TestCase(ItemUpdateData.ItemUpdateType.ItemState)]
    [TestCase(ItemUpdateData.ItemUpdateType.FullSync)]
    [TestCase(ItemUpdateData.ItemUpdateType.Create)]
    public void StateUpdatesRoundTripTheirMoveCause(ItemUpdateData.ItemUpdateType updateType)
    {
        ItemUpdateData source = Fixture(updateType, ItemState.InHand);
        source.MoveCause = new ChangeCause<ItemMoveReason>(ItemMoveReason.TakenFromPlayer, 3, "Bentgear29", 77,
            "Liam's inventory -> Bentgear29's hand");

        ItemUpdateData result = RoundTrip(source);

        Assert.That(result.MoveCause.HasValue, Is.True);
        Assert.Multiple(() => {
            Assert.That(result.MoveCause.Value.ToString(),
                Is.EqualTo("TakenFromPlayer by Bentgear29 (Liam's inventory -> Bentgear29's hand)"));
            Assert.That(result.MoveCause.Value.PlayerId, Is.EqualTo(3));
            Assert.That(result.PlayerId, Is.EqualTo(source.PlayerId));
            Assert.That(result.RemovalCause, Is.Null);
        });
    }

    [TestCase(ItemUpdateData.ItemUpdateType.ItemState)]
    [TestCase(ItemUpdateData.ItemUpdateType.Destroy)]
    public void UpdatesWithoutACauseRoundTripWithout(ItemUpdateData.ItemUpdateType updateType)
    {
        ItemUpdateData result = RoundTrip(Fixture(updateType, ItemState.Dropped));
        Assert.Multiple(() => {
            Assert.That(result.MoveCause, Is.Null);
            Assert.That(result.RemovalCause, Is.Null);
        });
    }

    [Test]
    public void PositionAndObjectStateUpdatesCarryNoCause()
    {
        // Only a placement change carries one: the many pose and object-state updates stay as small.
        ItemUpdateData source = Fixture(ItemUpdateData.ItemUpdateType.ItemPosition | ItemUpdateData.ItemUpdateType.ObjectState,
            ItemState.Dropped);
        source.MoveCause = new ChangeCause<ItemMoveReason>(ItemMoveReason.Moved);
        var withCause = new NetDataWriter();
        ItemUpdateData.Serialize(withCause, source);
        source.MoveCause = null;
        var without = new NetDataWriter();
        ItemUpdateData.Serialize(without, source);

        Assert.That(withCause.Length, Is.EqualTo(without.Length));
        Assert.That(RoundTrip(source).MoveCause, Is.Null);
    }

    [Test]
    public void TemporaryAdoptionEnvelopeRoundTripsCompatibilityClassification()
    {
        ItemAdoptionRequestData source = new()
        {
            AdoptionToken = "temporary-token",
            PrefabName = "Flashlight",
            PlayerProperty = true,
            AuthoredItemKey = string.Empty,
            Position = new Vector3(12f, 34f, 56f),
            Rotation = Quaternion.identity,
            Snapshot = Fixture(ItemUpdateData.ItemUpdateType.FullSync, ItemState.InInventory)
        };
        var writer = new NetDataWriter();
        ItemAdoptionRequestData.Serialize(writer, source);
        var reader = new NetDataReader(writer.Data, 0, writer.Length);
        ItemAdoptionRequestData result = ItemAdoptionRequestData.Deserialize(reader);

        Assert.Multiple(() =>
        {
            Assert.That(reader.AvailableBytes, Is.Zero);
            Assert.That(result.AdoptionToken, Is.EqualTo(source.AdoptionToken));
            Assert.That(result.PrefabName, Is.EqualTo(source.PrefabName));
            Assert.That(result.PlayerProperty, Is.True);
            Assert.That(result.AuthoredItemKey, Is.Empty);
            Assert.That(result.Snapshot.UpdateType, Is.EqualTo(ItemUpdateData.ItemUpdateType.FullSync));
            Assert.That(result.Snapshot.ItemState, Is.EqualTo(ItemState.InInventory));
        });
        AssertVector(result.Position, source.Position);
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
        OriginatingPlayerId = 2,
        PrefabName = "Cup2",
        AuthoredItemKey = "0123456789abcdef0123456789abcdef",
        BelongsToPlayer = true,
        ItemState = state,
        ItemPosition = new Vector3(9469.9f, 120.599f, 13616.892f),
        ItemRotation = new Quaternion(.175f, .732f, -.211f, .624f),
        ThrowDirection = new Vector3(.839f, -.528f, -.132f),
        PlayerId = 2,
        PlayerHand = PlayerHand.Left,
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
        Assert.That(result.OriginatingPlayerId, Is.EqualTo(source.OriginatingPlayerId));
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
