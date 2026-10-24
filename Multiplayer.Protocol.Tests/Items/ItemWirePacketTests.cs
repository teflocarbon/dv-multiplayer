using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib.Utils;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Shared.Protocol.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace Multiplayer.Protocol.Tests.Items;

/// <summary>
/// The item wire (docs/design/items/wire.md): one update, one request, one result, one list, and
/// no GUID on any of them.
/// </summary>
[TestFixture]
public sealed class ItemWirePacketTests
{
    [Test]
    public void NoItemPacketCarriesAGuid()
    {
        Type[] packets = typeof(ClientboundItemUpdatesPacket).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Multiplayer.Shared.Protocol.Packets", StringComparison.Ordinal) == true &&
                        t.Name.Contains("Item") && t.IsClass && !t.IsAbstract)
            .ToArray();
        Assert.That(packets, Has.Length.GreaterThanOrEqualTo(5));
        List<string> guids = new();
        foreach (Type packet in packets)
            FindGuids(packet, packet.Name, new HashSet<Type>(), guids);
        Assert.That(guids, Is.Empty, "item packet members that are, or hold, a Guid");
    }

    [Test]
    public void ThePacketsOfTheWireAreTheItemsNamespace()
    {
        string[] names = typeof(ClientboundItemUpdatesPacket).Assembly.GetTypes()
            .Where(t => t.Namespace == "Multiplayer.Shared.Protocol.Packets.Items")
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.That(names, Is.EqualTo(new[]
        {
            nameof(ClientboundInventoryRowsPacket), nameof(ClientboundItemListPacket), nameof(ClientboundItemResultPacket),
            nameof(ClientboundItemUpdatesPacket),
            nameof(ServerboundItemListRequestPacket), nameof(ServerboundItemRequestPacket)
        }));
    }

    [Test]
    public void Updates_RoundTripEachRecipientsRelationAndOnlyTheClaimantsClaim()
    {
        ItemUpdate update = new()
        {
            UpdateType = ItemUpdate.ItemUpdateType.ItemState, ItemNetId = 78, AuthorityRevision = 42,
            ItemState = ItemState.InInventory, PlayerId = 2, InventoryClaimSlot = 4,
            InventoryClaimFlags = ItemInventoryClaimFlags.Reserved, OriginatingPlayerId = 2
        };
        ClientboundItemUpdatesPacket result = RoundTrip(new ClientboundItemUpdatesPacket
        {
            Updates = new List<ItemUpdate>
            {
                update.For(ItemRelation.Mine, claimant: true),
                update.For(ItemRelation.SomeoneElses, claimant: false),
                update.For(ItemRelation.Nobodys, claimant: false)
            }
        }, p => new ClientboundItemUpdatesPacket { Updates = p.Updates.ToList() });

        Assert.That(result.Updates, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(result.Updates.Select(u => u.Relation),
                Is.EqualTo(new[] { ItemRelation.Mine, ItemRelation.SomeoneElses, ItemRelation.Nobodys }));
            Assert.That(result.Updates[0].InventoryClaimSlot, Is.EqualTo(4));
            Assert.That(result.Updates[0].InventoryClaimFlags, Is.EqualTo(ItemInventoryClaimFlags.Reserved));
            Assert.That(result.Updates[1].HasClaim, Is.False);
            Assert.That(result.Updates[1].InventoryClaimSlot, Is.EqualTo(-1));
            Assert.That(result.Updates.All(u => u.PlayerId == 2 && u.AuthorityRevision == 42), Is.True);
        });
    }

    [Test]
    public void InventoryRows_RoundTripEachRowWithItsStateAndWhetherTheyAreTheWholeInventory()
    {
        ItemUpdate row = new()
        {
            UpdateType = ItemUpdate.ItemUpdateType.Create, ItemNetId = 78, PrefabName = "Lighter", AuthorityRevision = 1,
            ItemState = ItemState.InInventory, PlayerId = 2, InventorySlot = 7, InventoryClaimSlot = 7
        };
        ClientboundInventoryRowsPacket result = RoundTrip(new ClientboundInventoryRowsPacket
        {
            Full = true,
            Rows = new List<ItemUpdate> { row.For(ItemRelation.Mine, claimant: true), row.For(ItemRelation.Nobodys, claimant: false) },
            States = new[] { "{\"fuel\":0.5}", "" }
        }, p => new ClientboundInventoryRowsPacket { Full = p.Full, Rows = p.Rows.ToList(), States = p.States.ToArray() });

        Assert.That(result.Rows.Select(r => r.InventorySlot), Is.EqualTo(new[] { 7, 7 }));
        Assert.That(result.Rows[0].InventoryClaimSlot, Is.EqualTo(7));
        Assert.That(result.Rows[0].PrefabName, Is.EqualTo("Lighter"));
        Assert.That(result.States, Is.EqualTo(new[] { "{\"fuel\":0.5}", "" }));
        Assert.That(result.Full, Is.True);
    }

    [Test]
    public void AnUpdateWithoutAClaimIsSmallerByTheClaim()
    {
        ItemUpdate claimed = new() { UpdateType = ItemUpdate.ItemUpdateType.ItemState, ItemNetId = 1, ItemState = ItemState.InInventory, InventoryClaimSlot = 3 };
        NetDataWriter with = new(), without = new();
        ItemUpdate.Serialize(with, claimed);
        ItemUpdate.Serialize(without, claimed.For(ItemRelation.SomeoneElses, false));
        Assert.That(with.Length - without.Length, Is.EqualTo(sizeof(int) + sizeof(byte)));
    }

    private static IEnumerable<ItemRequest> Requests()
    {
        yield return new ItemRequest { RequestId = 1, ItemNetId = 78, ExpectedRevision = 5, Action = ItemAction.Take, Hand = PlayerHand.Left, States = new() { ["on"] = true } };
        yield return new ItemRequest { RequestId = 2, ItemNetId = 78, ExpectedRevision = 6, Action = ItemAction.Stash, Slot = 3, ClaimFlags = ItemInventoryClaimFlags.Reserved, SavedState = "{\"Lighter_state\":{\"fuel\":0.25}}" };
        yield return new ItemRequest { RequestId = 3, ItemNetId = 78, ExpectedRevision = 7, Action = ItemAction.MoveSlot, Slot = 5 };
        yield return new ItemRequest
        {
            RequestId = 4, ItemNetId = 78, ExpectedRevision = 8, Action = ItemAction.Drop,
            Position = new Vector3(9469.9f, 120.6f, 13616.9f), Rotation = new Quaternion(.175f, .732f, -.211f, .624f),
            WorldParentKind = ItemWorldParentKind.TrainInterior, WorldParentNetId = 91,
            ParentLocalPosition = new Vector3(1.5f, .25f, -3f), ParentLocalRotation = Quaternion.identity
        };
        yield return new ItemRequest
        {
            RequestId = 5, ItemNetId = 78, ExpectedRevision = 9, Action = ItemAction.Throw,
            Position = new Vector3(1, 2, 3), Rotation = Quaternion.identity, ThrowDirection = new Vector3(.8f, -.5f, -.1f),
            WorldParentKind = ItemWorldParentKind.StaticParent, WorldParentKey = "fedcba9876543210fedcba9876543210",
            ParentLocalPosition = Vector3.one, ParentLocalRotation = Quaternion.identity
        };
        yield return new ItemRequest { RequestId = 6, ItemNetId = 78, ExpectedRevision = 10, Action = ItemAction.Recall, Slot = 4 };
        yield return new ItemRequest { RequestId = 7, ItemHandle = 51, Action = ItemAction.Retrieve };
        yield return new ItemRequest { RequestId = 8, ItemHandle = 52, ExpectedRevision = 3, Action = ItemAction.CollectLost, Slot = -1, InventoryCapacity = 12, OccupiedSlots = new[] { 0, 1, 2 } };
        yield return new ItemRequest { RequestId = 9, ItemNetId = 78, ExpectedRevision = 11, Action = ItemAction.Deposit, Container = 17, ShellNetId = 800, Slot = 2 };
        yield return new ItemRequest { RequestId = 10, ItemHandle = 53, ExpectedRevision = 11, Action = ItemAction.Withdraw, Slot = 6 };
        yield return new ItemRequest { RequestId = 11, ItemHandle = 53, ExpectedRevision = 12, Action = ItemAction.MoveInContainer, Container = 18, ContainerRevision = 12, Slot = 1 };
        yield return new ItemRequest { RequestId = 12, ItemNetId = 79, Action = ItemAction.DepositMoney };
        yield return new ItemRequest { RequestId = 13, ItemNetId = 78, ExpectedRevision = 13, Action = ItemAction.ReportBelowMap, SurfaceHeight = 117.5f };
        yield return new ItemRequest { RequestId = 14, ItemNetId = 78, ExpectedRevision = 14, Action = ItemAction.Attach, CarNetId = 22, Front = true, States = new() { ["lit"] = 1 } };
        yield return new ItemRequest { RequestId = 15, ItemNetId = 78, ExpectedRevision = 15, Action = ItemAction.SetState, WorldInteraction = true, States = new() { ["notch"] = 2 } };
        yield return new ItemRequest { RequestId = 16, ItemNetId = 81, ExpectedRevision = 16, Action = ItemAction.LoadMagazine, MagazineNetId = 82, MagazineSlot = 0, States = new() { ["track"] = 3 } };
    }

    [TestCaseSource(nameof(Requests))]
    public void Request_RoundTripsItsActionsArguments(ItemRequest source)
    {
        ItemRequest result = RoundTrip(new ServerboundItemRequestPacket { Request = source },
            p => new ServerboundItemRequestPacket { Request = p.Request }).Request;

        Assert.That(Normalized(result), Is.EqualTo(Normalized(source)));
    }

    [Test]
    public void EveryActionHasAFixture()
    {
        ItemAction[] covered = Requests().Select(r => r.Action).Distinct().ToArray();
        ItemAction[] all = Enum.GetValues(typeof(ItemAction)).Cast<ItemAction>().Where(a => a != ItemAction.None).ToArray();
        Assert.That(covered, Is.EquivalentTo(all));
    }

    [Test]
    public void Request_CarriesOnlyItsActionsArguments()
    {
        ItemRequest take = new() { RequestId = 1, ItemNetId = 2, Action = ItemAction.Take, Slot = 9, Container = 4, Position = Vector3.one };
        ItemRequest result = RoundTrip(new ServerboundItemRequestPacket { Request = take }, p => new ServerboundItemRequestPacket { Request = p.Request }).Request;
        Assert.Multiple(() =>
        {
            Assert.That(result.Slot, Is.EqualTo(-1));
            Assert.That(result.Container, Is.Zero);
            Assert.That(result.Position, Is.EqualTo(Vector3.zero));
        });
    }

    [TestCase(ItemRefusal.None, "")]
    [TestCase(ItemRefusal.StaleRevision, "lost-item-revision-changed")]
    [TestCase(ItemRefusal.Expired, "request-expired")]
    public void Result_RoundTripsTheRequestAndTheAnswer(ItemRefusal refusal, string detail)
    {
        ClientboundItemResultPacket result = RoundTrip(new ClientboundItemResultPacket
        {
            RequestId = 7, Refusal = refusal, Detail = detail, Revision = 43, ItemNetId = 766
        }, p => new ClientboundItemResultPacket { RequestId = p.RequestId, Refusal = p.Refusal, Detail = p.Detail, Revision = p.Revision, ItemNetId = p.ItemNetId });

        Assert.Multiple(() =>
        {
            Assert.That(result.RequestId, Is.EqualTo(7));
            Assert.That(result.Refusal, Is.EqualTo(refusal));
            Assert.That(result.Accepted, Is.EqualTo(refusal == ItemRefusal.None));
            Assert.That(result.Detail, Is.EqualTo(detail));
            Assert.That(result.Revision, Is.EqualTo(43));
            Assert.That(result.ItemNetId, Is.EqualTo(766));
        });
    }

    [TestCase(ItemListKind.LostAndFound, (ushort)0, 0u)]
    [TestCase(ItemListKind.Owned, (ushort)0, 0u)]
    [TestCase(ItemListKind.Container, (ushort)800, 0u)]
    [TestCase(ItemListKind.Container, (ushort)0, 17u)]
    public void ListRequest_RoundTripsWhichList(ItemListKind kind, ushort shell, uint container)
    {
        ServerboundItemListRequestPacket result = RoundTrip(new ServerboundItemListRequestPacket
        {
            RequestId = 12, Kind = kind, ShellNetId = shell, Container = container
        }, p => new ServerboundItemListRequestPacket { RequestId = p.RequestId, Kind = p.Kind, ShellNetId = p.ShellNetId, Container = p.Container });

        Assert.Multiple(() =>
        {
            Assert.That(result.RequestId, Is.EqualTo(12));
            Assert.That(result.Kind, Is.EqualTo(kind));
            Assert.That(result.ShellNetId, Is.EqualTo(shell));
            Assert.That(result.Container, Is.EqualTo(container));
        });
    }

    [Test]
    public void List_RoundTripsEveryRowInOrder()
    {
        ItemRow[] rows =
        {
            new() { Handle = 1, PrefabName = "CommsRadio", DisplayName = "Radio", Where = "OwnerDistance", Slot = 0, Revision = 5 },
            new() { Handle = 2, PrefabName = "Lantern", DisplayName = "Lantern", Where = "in L-042", Flags = ItemRowFlags.Retrievable, Revision = 8 },
            new() { Handle = 3, PrefabName = "Mug", Flags = ItemRowFlags.SomeoneElses | ItemRowFlags.Container, Slot = 7 }
        };
        ClientboundItemListPacket result = RoundTrip(new ClientboundItemListPacket
        {
            RequestId = 11, Kind = ItemListKind.Container, Container = 17, Revision = 9, Capacity = 12, Rows = rows
        }, p => new ClientboundItemListPacket
        {
            RequestId = p.RequestId, Kind = p.Kind, Refusal = p.Refusal, Detail = p.Detail, Container = p.Container,
            Revision = p.Revision, Capacity = p.Capacity, Rows = p.Rows
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(result.RequestId, Is.EqualTo(11));
            Assert.That(result.Container, Is.EqualTo(17));
            Assert.That(result.Capacity, Is.EqualTo(12));
            Assert.That(result.Rows.Select(r => r.Handle), Is.EqualTo(new uint[] { 1, 2, 3 }));
            Assert.That(result.Rows[1].Where, Is.EqualTo("in L-042"));
            Assert.That(result.Rows[1].Flags, Is.EqualTo(ItemRowFlags.Retrievable));
            Assert.That(result.Rows[2].Flags, Is.EqualTo(ItemRowFlags.SomeoneElses | ItemRowFlags.Container));
            Assert.That(result.Rows.Select(r => r.Slot), Is.EqualTo(new[] { 0, -1, 7 }));
        });
    }

    [Test]
    public void List_RoundTripsARefusal()
    {
        ClientboundItemListPacket result = RoundTrip(new ClientboundItemListPacket
        {
            RequestId = 4, Kind = ItemListKind.Container, Refusal = ItemRefusal.NotYours, Detail = "container-foreign"
        }, p => new ClientboundItemListPacket { RequestId = p.RequestId, Kind = p.Kind, Refusal = p.Refusal, Detail = p.Detail, Rows = p.Rows });

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Refusal, Is.EqualTo(ItemRefusal.NotYours));
            Assert.That(result.Detail, Is.EqualTo("container-foreign"));
            Assert.That(result.Rows, Is.Empty);
        });
    }

    // ---- what the local game observed, as a request, and the change the server applies for it

    [TestCase(ItemState.InHand, ItemAction.Take)]
    [TestCase(ItemState.InInventory, ItemAction.Stash)]
    [TestCase(ItemState.Dropped, ItemAction.Drop)]
    [TestCase(ItemState.Thrown, ItemAction.Throw)]
    [TestCase(ItemState.Attached, ItemAction.Attach)]
    [TestCase(ItemState.InMagazine, ItemAction.LoadMagazine)]
    public void AnObservedMoveIsARequest_AndTranslatesBackToTheSameChange(ItemState state, ItemAction action)
    {
        ItemUpdate observed = Observed(ItemUpdate.ItemUpdateType.ItemState | ItemUpdate.ItemUpdateType.ObjectState, state);

        ItemRequest request = OverTheWire(ItemRequestTranslation.FromObservation(observed));
        Assert.That(request.Action, Is.EqualTo(action));
        Assert.That(request.ExpectedRevision, Is.EqualTo(observed.AuthorityRevision));

        ItemUpdate change = ItemRequestTranslation.ToChange(request, 3);
        Assert.Multiple(() =>
        {
            Assert.That(change.ItemNetId, Is.EqualTo(observed.ItemNetId));
            Assert.That(change.ItemState, Is.EqualTo(state));
            Assert.That(change.AuthorityRevision, Is.EqualTo(observed.AuthorityRevision));
            Assert.That(change.OriginatingPlayerId, Is.EqualTo(3));
            Assert.That(change.States, Is.EqualTo(observed.States));
            Assert.That(ItemUpdate.IncludesItemState(change.UpdateType), Is.True);
            Assert.That(change.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.ObjectState), Is.True);
            Assert.That(change.TransitionReason, Is.EqualTo(ItemTransitionReason.ClientState));
        });
        switch (state)
        {
            case ItemState.InHand:
                Assert.That(change.PlayerHand, Is.EqualTo(PlayerHand.Right));
                break;
            case ItemState.InInventory:
                // The slot it's in; the claim is the ledger's, not the requester's.
                Assert.That(change.InventorySlot, Is.EqualTo(11));
                Assert.That(change.InventoryClaimSlot, Is.EqualTo(-1));
                break;
            case ItemState.Dropped:
            case ItemState.Thrown:
                Assert.That(change.ItemPosition, Is.EqualTo(observed.ItemPosition));
                Assert.That(change.WorldParentKind, Is.EqualTo(ItemWorldParentKind.TrainInterior));
                Assert.That(change.WorldParentNetId, Is.EqualTo(91));
                if (state == ItemState.Thrown)
                    Assert.That(change.ThrowDirection, Is.EqualTo(observed.ThrowDirection));
                break;
            case ItemState.Attached:
                Assert.That(change.CarNetId, Is.EqualTo(22));
                Assert.That(change.AttachedFront, Is.True);
                break;
            case ItemState.InMagazine:
                // The tool and its slot, which the server checks (LiveItems.MagazineRefusal).
                Assert.That(change.MagazineNetId, Is.EqualTo(23));
                Assert.That(change.MagazineSlot, Is.EqualTo(1));
                break;
        }
        // Only a stash names a slot: every other move leaves the server's record's slot alone.
        if (state != ItemState.InInventory)
            Assert.That(change.InventorySlot, Is.EqualTo(-1));
    }

    [Test]
    public void AStashWithoutItsSlot_AsksForItsClaim()
    {
        ItemUpdate observed = Observed(ItemUpdate.ItemUpdateType.ItemState, ItemState.InInventory);
        observed.InventorySlot = -1;

        Assert.That(ItemRequestTranslation.FromObservation(observed).Slot, Is.EqualTo(4));
    }

    [Test]
    public void ASlotMove_IsAStashIntoThatSlot()
    {
        ItemRequest request = OverTheWire(new ItemRequest
        {
            RequestId = 9, ItemNetId = 78, ExpectedRevision = 42, Action = ItemAction.MoveSlot, Slot = 20
        });

        ItemUpdate change = ItemRequestTranslation.ToChange(request, 3);

        Assert.Multiple(() =>
        {
            Assert.That(change.ItemState, Is.EqualTo(ItemState.InInventory));
            Assert.That(change.PlayerId, Is.EqualTo(3));
            Assert.That(change.InventorySlot, Is.EqualTo(20));
            Assert.That(change.InventoryClaimSlot, Is.EqualTo(-1));
            Assert.That(change.AuthorityRevision, Is.EqualTo(42));
        });
    }

    // A nearby player working an item where it lies: the server takes it only as an interaction.
    [TestCase(ItemTransitionReason.ClientState)]
    [TestCase(ItemTransitionReason.ClientInteraction)]
    public void AnObservedStateChangeAloneIsASetState_KeepingWhetherItWasAnInteraction(ItemTransitionReason reason)
    {
        ItemUpdate observed = Observed(ItemUpdate.ItemUpdateType.ObjectState, ItemState.Dropped);
        observed.TransitionReason = reason;

        ItemRequest request = OverTheWire(ItemRequestTranslation.FromObservation(observed));
        ItemUpdate change = ItemRequestTranslation.ToChange(request, 3);

        Assert.Multiple(() =>
        {
            Assert.That(request.Action, Is.EqualTo(ItemAction.SetState));
            Assert.That(change.UpdateType, Is.EqualTo(ItemUpdate.ItemUpdateType.ObjectState));
            Assert.That(change.States, Is.EqualTo(observed.States));
            Assert.That(change.TransitionReason, Is.EqualTo(reason));
        });
    }

    [TestCase(ItemUpdate.ItemUpdateType.Create)]
    [TestCase(ItemUpdate.ItemUpdateType.Destroy)]
    [TestCase(ItemUpdate.ItemUpdateType.Create | ItemUpdate.ItemUpdateType.ObjectState)]
    public void APlayerCannotAskToCreateOrDestroy(ItemUpdate.ItemUpdateType updateType) =>
        Assert.That(ItemRequestTranslation.FromObservation(Observed(updateType, ItemState.Dropped)), Is.Null);

    [Test]
    public void ProjectorsCoverEveryPacketOfTheWire()
    {
        object[] packets =
        {
            new ClientboundItemUpdatesPacket { Updates = new List<ItemUpdate> { Observed(ItemUpdate.ItemUpdateType.FullSync, ItemState.Thrown) } },
            new ServerboundItemRequestPacket { Request = Requests().First(r => r.Action == ItemAction.Throw) },
            new ClientboundItemResultPacket { RequestId = 3, Refusal = ItemRefusal.Busy, Detail = "recall-busy" },
            new ServerboundItemListRequestPacket { RequestId = 4, Kind = ItemListKind.Container, ShellNetId = 800 },
            new ClientboundItemListPacket { RequestId = 4, Kind = ItemListKind.LostAndFound, Rows = new[] { new ItemRow { Handle = 1, PrefabName = "Mug" } } }
        };
        foreach (object packet in packets)
        {
            DebugPacketProjection projection = DebugPacketProjectorRegistry.Project(packet, 2);
            Dictionary<string, object> wire = (Dictionary<string, object>)projection.Wire;
            Assert.That(wire["packetType"], Is.EqualTo(packet.GetType().Name));
            Assert.That(wire.ContainsKey("fields"), Is.False, packet.GetType().Name + " fell back to the strict snapshotter");
            Assert.That(wire.ContainsKey("projectionError"), Is.False, packet.GetType().Name + ": " + (wire.TryGetValue("projectionError", out object e) ? e : ""));
            Assert.That(projection.Summary, Is.Not.Empty.And.Not.EqualTo(packet.GetType().Name));
        }
    }

    private static ItemUpdate Observed(ItemUpdate.ItemUpdateType updateType, ItemState state) => new()
    {
        UpdateType = updateType,
        ItemNetId = 78,
        AuthorityRevision = 42,
        ItemState = state,
        PlayerId = 3,
        PlayerHand = PlayerHand.Right,
        InventorySlot = 11,
        InventoryClaimSlot = 4,
        InventoryClaimFlags = ItemInventoryClaimFlags.Reserved,
        TransitionReason = ItemTransitionReason.ClientState,
        ItemPosition = new Vector3(9469.9f, 120.6f, 13616.9f),
        ItemRotation = new Quaternion(.175f, .732f, -.211f, .624f),
        ThrowDirection = new Vector3(.8f, -.5f, -.1f),
        WorldParentKind = ItemWorldParentKind.TrainInterior,
        WorldParentNetId = 91,
        ParentLocalPosition = new Vector3(1.5f, .25f, -3f),
        ParentLocalRotation = Quaternion.identity,
        CarNetId = 22,
        AttachedFront = true,
        MagazineNetId = 23,
        MagazineSlot = 1,
        States = new Dictionary<string, object> { ["on"] = true }
    };

    private static ItemRequest OverTheWire(ItemRequest request) =>
        RoundTrip(new ServerboundItemRequestPacket { Request = request }, p => new ServerboundItemRequestPacket { Request = p.Request }).Request;

    private static T RoundTrip<T>(T packet, Func<T, T> copy) where T : class, new()
    {
        T result = null;
        NetPacketProcessor processor = new();
        PacketSerializationRegistry.Register(processor);
        processor.SubscribeReusable<T>(received => result = copy(received));
        NetDataWriter writer = new();
        processor.Write(writer, packet);
        processor.ReadAllPackets(new NetDataReader(writer.CopyData()));
        Assert.That(result, Is.Not.Null, typeof(T).Name + " did not arrive");
        return result;
    }

    // A request's fields as text, so a fixture compares field by field.
    private static string Normalized(ItemRequest request) => string.Join("\n",
        typeof(ItemRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + Text(p.GetValue(request))));

    private static string Text(object value) => value switch
    {
        null => "null",
        Vector3 v => $"({v.x:0.###},{v.y:0.###},{v.z:0.###})",
        Quaternion q => $"({q.x:0.###},{q.y:0.###},{q.z:0.###},{q.w:0.###})",
        IDictionary d => "{" + string.Join(",", d.Keys.Cast<object>().Select(k => k + ":" + d[k])) + "}",
        IEnumerable e and not string => "[" + string.Join(",", e.Cast<object>()) + "]",
        _ => value.ToString()
    };

    private static void FindGuids(Type type, string path, HashSet<Type> seen, List<string> found)
    {
        if (!seen.Add(type))
            return;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Check(property.PropertyType, path + "." + property.Name, seen, found);
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Check(field.FieldType, path + "." + field.Name, seen, found);
    }

    // A member's type, or what an array, list, dictionary or nullable of it holds, checked for a
    // Guid all the way down through the mod's own types.
    private static void Check(Type type, string path, HashSet<Type> seen, List<string> found)
    {
        if (type == typeof(Guid))
        {
            found.Add(path);
            return;
        }
        if (type.IsArray)
        {
            Check(type.GetElementType(), path + "[]", seen, found);
            return;
        }
        if (type.IsGenericType)
            foreach (Type argument in type.GetGenericArguments())
                Check(argument, path + "<" + argument.Name + ">", seen, found);
        if (Ours(type) && !type.IsEnum && !type.IsPrimitive)
            FindGuids(type, path, seen, found);
    }

    private static bool Ours(Type type) =>
        type.Assembly == typeof(ItemUpdate).Assembly || type.Assembly == typeof(ItemRelation).Assembly;
}
