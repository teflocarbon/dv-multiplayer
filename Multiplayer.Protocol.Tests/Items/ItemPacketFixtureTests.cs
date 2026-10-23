using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Debugging.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests.Items;

[TestFixture]
public sealed class ItemPacketFixtureTests
{
    [TestCase(ItemUpdate.ItemUpdateType.ObjectState, false)]
    [TestCase(ItemUpdate.ItemUpdateType.ItemState, true)]
    [TestCase(ItemUpdate.ItemUpdateType.FullSync, true)]
    [TestCase(ItemUpdate.ItemUpdateType.Create, true)]
    [TestCase(ItemUpdate.ItemUpdateType.Create | ItemUpdate.ItemUpdateType.ObjectState, true)]
    public void PlacementDetectionDoesNotTreatObjectStateAsFullSync(
        ItemUpdate.ItemUpdateType updateType, bool expected)
    {
        Assert.That(ItemUpdate.IncludesItemState(updateType), Is.EqualTo(expected));
    }

    // A flashlight's battery ticking on the server's copy must not count as moving it (playtest 3).
    [TestCase(ItemUpdate.ItemUpdateType.ObjectState, false)]
    [TestCase(ItemUpdate.ItemUpdateType.None, false)]
    [TestCase(ItemUpdate.ItemUpdateType.ItemState, true)]
    [TestCase(ItemUpdate.ItemUpdateType.FullSync, true)]
    [TestCase(ItemUpdate.ItemUpdateType.Create, true)]
    public void ObjectStateAloneDoesNotChangePlacement(ItemUpdate.ItemUpdateType updateType, bool expected)
    {
        Assert.That(ItemUpdate.ChangesPlacement(updateType), Is.EqualTo(expected));
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
    [TestCase(ItemState.Installed)]
    public void ItemStateRoundTripsOnlyItsWireSemanticFields(ItemState state)
    {
        ItemUpdate source = Fixture(ItemUpdate.ItemUpdateType.ItemState, state);
        ItemUpdate result = RoundTrip(source);

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
            case ItemState.Installed:
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
        ItemUpdate result = RoundTrip(Fixture(ItemUpdate.ItemUpdateType.Create, ItemState.Dropped));
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
        ItemUpdate source = Fixture(ItemUpdate.ItemUpdateType.ItemState, ItemState.Dropped);
        source.WorldParentKind = kind;
        source.WorldParentNetId = 91;
        source.WorldParentKey = "fedcba9876543210fedcba9876543210";
        source.ParentLocalPosition = new Vector3(1.5f, .25f, -3f);
        source.ParentLocalRotation = new Quaternion(.182574f, .365148f, .547723f, .730297f);

        ItemUpdate result = RoundTrip(source);

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
        ItemUpdate result = RoundTrip(Fixture(ItemUpdate.ItemUpdateType.Destroy, ItemState.Thrown));
        Assert.Multiple(() => {
            Assert.That(result.UpdateType, Is.EqualTo(ItemUpdate.ItemUpdateType.Destroy));
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
        ItemUpdate source = Fixture(ItemUpdate.ItemUpdateType.Destroy, ItemState.Dropped);
        source.RemovalCause = new ChangeCause<ItemRemovalReason>(ItemRemovalReason.CollectedToLostAndFound,
            0, null, 9001, "OwnerDistance, owner Bentgear29");
        source.MoveCause = new ChangeCause<ItemMoveReason>(ItemMoveReason.Dropped); // not on a Destroy

        ItemUpdate result = RoundTrip(source);

        Assert.That(result.RemovalCause.HasValue, Is.True);
        Assert.Multiple(() => {
            Assert.That(result.RemovalCause.Value.Reason, Is.EqualTo(ItemRemovalReason.CollectedToLostAndFound));
            Assert.That(result.RemovalCause.Value.Tick, Is.EqualTo(9001));
            Assert.That(result.RemovalCause.Value.Detail, Is.EqualTo("OwnerDistance, owner Bentgear29"));
            Assert.That(result.MoveCause, Is.Null);
        });
    }

    [TestCase(ItemUpdate.ItemUpdateType.ItemState)]
    [TestCase(ItemUpdate.ItemUpdateType.FullSync)]
    [TestCase(ItemUpdate.ItemUpdateType.Create)]
    public void StateUpdatesRoundTripTheirMoveCause(ItemUpdate.ItemUpdateType updateType)
    {
        ItemUpdate source = Fixture(updateType, ItemState.InHand);
        source.MoveCause = new ChangeCause<ItemMoveReason>(ItemMoveReason.TakenFromPlayer, 3, "Bentgear29", 77,
            "Liam's inventory -> Bentgear29's hand");

        ItemUpdate result = RoundTrip(source);

        Assert.That(result.MoveCause.HasValue, Is.True);
        Assert.Multiple(() => {
            Assert.That(result.MoveCause.Value.ToString(),
                Is.EqualTo("TakenFromPlayer by Bentgear29 (Liam's inventory -> Bentgear29's hand)"));
            Assert.That(result.MoveCause.Value.PlayerId, Is.EqualTo(3));
            Assert.That(result.PlayerId, Is.EqualTo(source.PlayerId));
            Assert.That(result.RemovalCause, Is.Null);
        });
    }

    [TestCase(ItemUpdate.ItemUpdateType.ItemState)]
    [TestCase(ItemUpdate.ItemUpdateType.Destroy)]
    public void UpdatesWithoutACauseRoundTripWithout(ItemUpdate.ItemUpdateType updateType)
    {
        ItemUpdate result = RoundTrip(Fixture(updateType, ItemState.Dropped));
        Assert.Multiple(() => {
            Assert.That(result.MoveCause, Is.Null);
            Assert.That(result.RemovalCause, Is.Null);
        });
    }

    [Test]
    public void ObjectStateUpdatesCarryNoCause()
    {
        // Only a placement change carries one: the many object-state updates stay as small.
        ItemUpdate source = Fixture(ItemUpdate.ItemUpdateType.ObjectState, ItemState.Dropped);
        source.MoveCause = new ChangeCause<ItemMoveReason>(ItemMoveReason.Moved);
        var withCause = new NetDataWriter();
        ItemUpdate.Serialize(withCause, source);
        source.MoveCause = null;
        var without = new NetDataWriter();
        ItemUpdate.Serialize(without, source);

        Assert.That(withCause.Length, Is.EqualTo(without.Length));
        Assert.That(RoundTrip(source).MoveCause, Is.Null);
    }

    [TestCase(ItemUpdate.ItemUpdateType.ObjectState)]
    [TestCase(ItemUpdate.ItemUpdateType.FullSync)]
    public void StateBearingUpdatesRoundTripTrackedState(ItemUpdate.ItemUpdateType updateType)
    {
        ItemUpdate result = RoundTrip(Fixture(updateType, ItemState.Thrown));
        Assert.That(result.States, Has.Count.EqualTo(5));
        if (updateType == ItemUpdate.ItemUpdateType.FullSync)
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
            new ClientboundItemUpdatesPacket { Updates = new List<ItemUpdate> { Fixture(ItemUpdate.ItemUpdateType.FullSync, ItemState.Thrown) } }, 2);
        string json = DebugJson.Serialize(projection.Wire);
        foreach (string forbidden in new[] { "normalized", "sqrMagnitude", "eulerAngles", "maximum depth" })
            Assert.That(json, Does.Not.Contain(forbidden).IgnoreCase);
    }

    [Test]
    public void UnsupportedTrackedTypeFailsBeforeProducingAmbiguousWireData()
    {
        ItemUpdate data = Fixture(ItemUpdate.ItemUpdateType.ObjectState, ItemState.Dropped);
        data.States = new Dictionary<string, object> { ["unsupported"] = new object() };
        Assert.Throws<NotSupportedException>(() => ItemUpdate.Serialize(new NetDataWriter(), data));
    }

    private static ItemUpdate RoundTrip(ItemUpdate source)
    {
        var writer = new NetDataWriter();
        ItemUpdate.Serialize(writer, source);
        var reader = new NetDataReader(writer.Data, 0, writer.Length);
        ItemUpdate result = ItemUpdate.Deserialize(reader);
        Assert.That(reader.AvailableBytes, Is.Zero, "fixture must consume the complete payload");
        return result;
    }

    private static ItemUpdate Fixture(ItemUpdate.ItemUpdateType updateType, ItemState state) => new()
    {
        UpdateType = updateType,
        ItemNetId = 78,
        AuthorityRevision = 42,
        Relation = ItemRelation.Mine,
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

    private static void AssertCommon(ItemUpdate source, ItemUpdate result) => Assert.Multiple(() => {
        Assert.That(result.UpdateType, Is.EqualTo(source.UpdateType));
        Assert.That(result.ItemNetId, Is.EqualTo(source.ItemNetId));
        Assert.That(result.AuthorityRevision, Is.EqualTo(source.AuthorityRevision));
        Assert.That(result.Relation, Is.EqualTo(source.Relation));
        Assert.That(result.InventoryClaimSlot, Is.EqualTo(source.InventoryClaimSlot));
        Assert.That(result.InventoryClaimFlags, Is.EqualTo(source.InventoryClaimFlags));
        Assert.That(result.TransitionReason, Is.EqualTo(source.TransitionReason));
        Assert.That(result.OriginatingPlayerId, Is.EqualTo(source.OriginatingPlayerId));
    });

    private static void AssertWorldTransform(ItemUpdate result)
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
