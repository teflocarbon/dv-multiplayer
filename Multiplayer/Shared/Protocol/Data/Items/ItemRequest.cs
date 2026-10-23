using System.Collections.Generic;
using LiteNetLib.Utils;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Serialization;
using UnityEngine;

namespace Multiplayer.Shared.Protocol.Data.Items;

/// <summary>What a player asks the server to do to an item. Byte-sized on the wire, append-only.</summary>
public enum ItemAction : byte
{
    None,
    /// <summary>Into a hand (<see cref="ItemRequest.Hand"/>), from the world, a car or the requester's inventory.</summary>
    Take,
    /// <summary>Into the requester's inventory at <see cref="ItemRequest.Slot"/>.</summary>
    Stash,
    /// <summary>To another slot (<see cref="ItemRequest.Slot"/>) of the requester's inventory; onto another of their items, the two swap.</summary>
    MoveSlot,
    /// <summary>Let go at a pose.</summary>
    Drop,
    /// <summary>Let go at a pose with a direction.</summary>
    Throw,
    /// <summary>An owner's essential back into its claim slot <see cref="ItemRequest.Slot"/>, from wherever it is.</summary>
    Recall,
    /// <summary>An owned-items row (a handle) into its owner's Lost and Found.</summary>
    Retrieve,
    /// <summary>A Lost and Found row (a handle) into the requester's inventory.</summary>
    CollectLost,
    /// <summary>Into a container (<see cref="ItemRequest.Container"/>) at <see cref="ItemRequest.Slot"/>.</summary>
    Deposit,
    /// <summary>A container row (a handle) into the requester's inventory at <see cref="ItemRequest.Slot"/>.</summary>
    Withdraw,
    /// <summary>A container row (a handle) to <see cref="ItemRequest.Slot"/> of <see cref="ItemRequest.Container"/>.</summary>
    MoveInContainer,
    /// <summary>A banknote or coin into the wallet.</summary>
    DepositMoney,
    /// <summary>The requester's game saw the item fall below the map (<see cref="ItemRequest.SurfaceHeight"/>).</summary>
    ReportBelowMap,
    /// <summary>Snapped to a car's end (an end-of-train lantern).</summary>
    Attach,
    /// <summary>Only its object state (<see cref="ItemRequest.States"/>) changed.</summary>
    SetState
}

/// <summary>
/// One request (docs/design/items/wire.md, "One request, one result"): a per-client
/// <see cref="RequestId"/> counting up, the item by NetId or by handle, the revision the requester
/// last saw, and an action with its arguments. Only the arguments the action uses are on the wire.
/// The server answers with one <c>ClientboundItemResultPacket</c>.
/// </summary>
public class ItemRequest
{
    public uint RequestId { get; set; }
    /// <summary>An item with a live object; 0 when <see cref="ItemHandle"/> names it.</summary>
    public ushort ItemNetId { get; set; }
    /// <summary>A row: a Lost and Found entry, an owned-items row, a container slot. 0 when <see cref="ItemNetId"/> names it.</summary>
    public uint ItemHandle { get; set; }
    public uint ExpectedRevision { get; set; }
    public ItemAction Action { get; set; }

    public int Slot { get; set; } = -1;
    public PlayerHand Hand { get; set; }
    /// <summary>A stash's claim on its slot (an essential's reserved slot), as the requester's inventory has it.</summary>
    public ItemInventoryClaimFlags ClaimFlags { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.identity;
    public ItemWorldParentKind WorldParentKind { get; set; }
    public ushort WorldParentNetId { get; set; }
    public string WorldParentKey { get; set; } = string.Empty;
    public Vector3 ParentLocalPosition { get; set; }
    public Quaternion ParentLocalRotation { get; set; } = Quaternion.identity;
    public Vector3 ThrowDirection { get; set; }
    public ushort CarNetId { get; set; }
    public bool Front { get; set; }
    /// <summary>A container's handle; for a deposit, 0 with <see cref="ShellNetId"/> for the open one.</summary>
    public uint Container { get; set; }
    public ushort ShellNetId { get; set; }
    /// <summary>The destination container's revision, for a move between two containers.</summary>
    public uint ContainerRevision { get; set; }
    public float SurfaceHeight { get; set; }
    /// <summary>
    /// For a Lost and Found collection: the requester's inventory, until the server keeps every
    /// inventory itself (R3).
    /// </summary>
    public int InventoryCapacity { get; set; }
    public int[] OccupiedSlots { get; set; } = System.Array.Empty<int>();
    /// <summary>Object state changed with the move (or alone, <see cref="ItemAction.SetState"/>).</summary>
    public Dictionary<string, object> States { get; set; }
    /// <summary>A <see cref="ItemAction.SetState"/> by a player working the item where it lies, not holding it.</summary>
    public bool WorldInteraction { get; set; }

    public static bool CarriesPose(ItemAction action) => action is ItemAction.Drop or ItemAction.Throw;

    public static bool CarriesStates(ItemAction action) => action is ItemAction.Take or ItemAction.Stash or
        ItemAction.Drop or ItemAction.Throw or ItemAction.Attach or ItemAction.SetState;

    public static void Serialize(NetDataWriter writer, ItemRequest data)
    {
        writer.Put(data.RequestId);
        writer.Put(data.ItemNetId);
        writer.Put(data.ItemHandle);
        writer.Put(data.ExpectedRevision);
        writer.Put((byte)data.Action);
        switch (data.Action)
        {
            case ItemAction.Take:
                writer.Put((byte)data.Hand);
                break;
            case ItemAction.Stash:
                writer.Put(data.Slot);
                writer.Put((byte)data.ClaimFlags);
                break;
            case ItemAction.MoveSlot:
            case ItemAction.Recall:
            case ItemAction.Withdraw:
                writer.Put(data.Slot);
                break;
            case ItemAction.Drop:
            case ItemAction.Throw:
                Vector3Serializer.Serialize(writer, data.Position);
                QuaternionSerializer.Serialize(writer, data.Rotation);
                writer.Put((byte)data.WorldParentKind);
                if (data.WorldParentKind == ItemWorldParentKind.TrainInterior)
                    writer.Put(data.WorldParentNetId);
                else if (data.WorldParentKind == ItemWorldParentKind.StaticParent)
                    writer.Put(data.WorldParentKey ?? string.Empty);
                if (data.WorldParentKind != ItemWorldParentKind.World)
                {
                    Vector3Serializer.Serialize(writer, data.ParentLocalPosition);
                    QuaternionSerializer.Serialize(writer, data.ParentLocalRotation);
                }
                if (data.Action == ItemAction.Throw)
                    Vector3Serializer.Serialize(writer, data.ThrowDirection);
                break;
            case ItemAction.CollectLost:
                writer.Put(data.Slot);
                writer.Put(data.InventoryCapacity);
                writer.PutArray(data.OccupiedSlots ?? System.Array.Empty<int>());
                break;
            case ItemAction.Deposit:
                writer.Put(data.Container);
                writer.Put(data.ShellNetId);
                writer.Put(data.Slot);
                break;
            case ItemAction.MoveInContainer:
                writer.Put(data.Container);
                writer.Put(data.ContainerRevision);
                writer.Put(data.Slot);
                break;
            case ItemAction.ReportBelowMap:
                writer.Put(data.SurfaceHeight);
                break;
            case ItemAction.Attach:
                writer.Put(data.CarNetId);
                writer.Put(data.Front);
                break;
            case ItemAction.SetState:
                writer.Put(data.WorldInteraction);
                break;
        }
        if (CarriesStates(data.Action))
            ItemUpdate.SerializeStates(writer, data.States);
    }

    public static ItemRequest Deserialize(NetDataReader reader)
    {
        ItemRequest data = new()
        {
            RequestId = reader.GetUInt(),
            ItemNetId = reader.GetUShort(),
            ItemHandle = reader.GetUInt(),
            ExpectedRevision = reader.GetUInt(),
            Action = (ItemAction)reader.GetByte()
        };
        switch (data.Action)
        {
            case ItemAction.Take:
                data.Hand = (PlayerHand)reader.GetByte();
                break;
            case ItemAction.Stash:
                data.Slot = reader.GetInt();
                data.ClaimFlags = (ItemInventoryClaimFlags)reader.GetByte();
                break;
            case ItemAction.MoveSlot:
            case ItemAction.Recall:
            case ItemAction.Withdraw:
                data.Slot = reader.GetInt();
                break;
            case ItemAction.Drop:
            case ItemAction.Throw:
                data.Position = Vector3Serializer.Deserialize(reader);
                data.Rotation = QuaternionSerializer.Deserialize(reader);
                data.WorldParentKind = (ItemWorldParentKind)reader.GetByte();
                if (data.WorldParentKind == ItemWorldParentKind.TrainInterior)
                    data.WorldParentNetId = reader.GetUShort();
                else if (data.WorldParentKind == ItemWorldParentKind.StaticParent)
                    data.WorldParentKey = reader.GetString();
                if (data.WorldParentKind != ItemWorldParentKind.World)
                {
                    data.ParentLocalPosition = Vector3Serializer.Deserialize(reader);
                    data.ParentLocalRotation = QuaternionSerializer.Deserialize(reader);
                }
                if (data.Action == ItemAction.Throw)
                    data.ThrowDirection = Vector3Serializer.Deserialize(reader);
                break;
            case ItemAction.CollectLost:
                data.Slot = reader.GetInt();
                data.InventoryCapacity = reader.GetInt();
                data.OccupiedSlots = reader.GetIntArray();
                break;
            case ItemAction.Deposit:
                data.Container = reader.GetUInt();
                data.ShellNetId = reader.GetUShort();
                data.Slot = reader.GetInt();
                break;
            case ItemAction.MoveInContainer:
                data.Container = reader.GetUInt();
                data.ContainerRevision = reader.GetUInt();
                data.Slot = reader.GetInt();
                break;
            case ItemAction.ReportBelowMap:
                data.SurfaceHeight = reader.GetFloat();
                break;
            case ItemAction.Attach:
                data.CarNetId = reader.GetUShort();
                data.Front = reader.GetBool();
                break;
            case ItemAction.SetState:
                data.WorldInteraction = reader.GetBool();
                break;
        }
        if (CarriesStates(data.Action))
            data.States = ItemUpdate.DeserializeStates(reader);
        return data;
    }
}
