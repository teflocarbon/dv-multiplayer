using LiteNetLib.Utils;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Serialization;
using Multiplayer.Core.Diagnostics;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Shared.Protocol.Data.Items;

/// <summary>
/// One item's change as the server sends it, built for each recipient
/// (docs/design/items/wire.md, "The update"): the recipient's relation to the item, never an owner,
/// and the claim only to its owner. A player's own move goes the other way as a request
/// (<c>ServerboundItemRequestPacket</c>), never as an update.
/// </summary>
public class ItemUpdate
{
    [Flags]
    public enum ItemUpdateType : byte
    {
        None = 0,
        Create = 1,
        Destroy = 2,
        ItemState = 4,
        ObjectState = 8,
        FullSync = ItemState | ObjectState,
    }

    /// <summary>
    /// Whether this update carries authoritative placement/holder state. FullSync contains
    /// ItemState and therefore returns true; ObjectState alone deliberately returns false.
    /// </summary>
    public static bool IncludesItemState(ItemUpdateType updateType) =>
        updateType.HasFlag(ItemUpdateType.ItemState) || updateType.HasFlag(ItemUpdateType.Create);

    /// <summary>
    /// Whether an accepted update changes where the item is (its placement, holder or pose), not
    /// just its object state. Masking with FullSync instead matched a plain ObjectState update:
    /// every flicker of the server's copy of a flashlight and every tick of its battery moved
    /// the item, so its holder's own changes then arrived on a stale revision and were refused.
    /// </summary>
    public static bool ChangesPlacement(ItemUpdateType updateType) => IncludesItemState(updateType);

    public ItemUpdateType UpdateType { get; set; }
    public ushort ItemNetId { get; set; }
    public string PrefabName { get; set; }
    public ItemState ItemState { get; set; }
    public Vector3 ItemPosition { get; set; }
    public Quaternion ItemRotation { get; set; }
    public Vector3 ThrowDirection { get; set; }
    /// <summary>The holder's session number (in a hand or an inventory), the one player number an update keeps.</summary>
    public byte PlayerId { get; set; }
    public ushort CarNetId { get; set; }
    public bool AttachedFront  { get; set; }
    public Dictionary<string, object> States { get; set; }
    public PlayerHand PlayerHand { get; set; }
    /// <summary>The holder's inventory slot it's in (<see cref="ItemState.InInventory"/>), for every inventory item; else -1.</summary>
    public int InventorySlot { get; set; } = -1;
    public uint AuthorityRevision { get; set; }

    /// <summary>
    /// The recipient's relation to the item (docs/design/items/wire.md, "Naming a player"): the wire
    /// never names an owner. The server builds one update per recipient
    /// (<c>NetworkServer.SendItemUpdates</c>); on the server's own copy it's the host's player's.
    /// </summary>
    public ItemRelation Relation { get; set; }
    /// <summary>
    /// The claim on an inventory slot: sent only to its claimant, the owner (or, for an unowned item,
    /// its holder: where a withdrawal puts it), -1 and none for everyone else.
    /// </summary>
    public int InventoryClaimSlot { get; set; } = -1;
    public ItemInventoryClaimFlags InventoryClaimFlags { get; set; }
    public ItemTransitionReason TransitionReason { get; set; }
    /// <summary>
    /// The session number of the player whose request made this change, 0 for the server: the
    /// cause's actor, carried on every update (a cause isn't), so a player's game knows its own
    /// change coming back.
    /// </summary>
    public byte OriginatingPlayerId { get; set; }
    /// <summary>
    /// Why the item went away. On a Destroy only, and only when the host knows why: not for a
    /// recipient it merely left the interest of.
    /// </summary>
    public ChangeCause<ItemRemovalReason>? RemovalCause { get; set; }
    /// <summary>
    /// Why its placement, holder or owner changed. Host to client only, on the update that carries
    /// the change (one with item state), so updates that only move or restate an item stay small.
    /// </summary>
    public ChangeCause<ItemMoveReason>? MoveCause { get; set; }
    /// <summary>Stable identity of a scene-authored representation. Present only on Create.</summary>
    public string AuthoredItemKey { get; set; }
    /// <summary>
    /// Derail Valley's durable personal-item classification. Create carries this because an
    /// interest-retired projection may have had all local storage membership quarantined.
    /// </summary>
    public bool BelongsToPlayer { get; set; }
    public ItemWorldParentKind WorldParentKind { get; set; }
    public ushort WorldParentNetId { get; set; }
    public string WorldParentKey { get; set; }
    public Vector3 ParentLocalPosition { get; set; }
    public Quaternion ParentLocalRotation { get; set; }
    /// <summary>The tool whose magazine it's in (<see cref="ItemState.InMagazine"/>), by its NetId; else 0.</summary>
    public ushort MagazineNetId { get; set; }
    /// <summary>Its slot of that magazine, or -1.</summary>
    public int MagazineSlot { get; set; } = -1;

    // Detached local observability correlation. This is deliberately not serialized.
    internal string DebugCorrelationFingerprint { get; set; }

    public static void Serialize(NetDataWriter writer, ItemUpdate data)
    {
        writer.Put((byte)data.UpdateType);
        writer.Put(data.ItemNetId);
        writer.Put(data.AuthorityRevision);
        writer.Put((byte)data.Relation);
        bool claim = data.HasClaim;
        writer.Put(claim);
        if (claim)
        {
            writer.Put(data.InventoryClaimSlot);
            writer.Put((byte)data.InventoryClaimFlags);
        }
        writer.Put((byte)data.TransitionReason);
        writer.Put(data.OriginatingPlayerId);

        if (data.UpdateType == ItemUpdateType.Destroy)
        {
            SerializeCause(writer, data.RemovalCause);
            return;
        }
        if (IncludesItemState(data.UpdateType))
            SerializeCause(writer, data.MoveCause);

        WireHome home = HomeOf(data);
        writer.Put((byte)home);

        if (data.UpdateType.HasFlag(ItemUpdateType.Create))
        {
            writer.Put(data.PrefabName);
            writer.Put(data.AuthoredItemKey ?? string.Empty);
            writer.Put(data.BelongsToPlayer);
        }

        // The home, as a union: each kind carries only its own fields (OI-SYNC-21).
        if (IncludesItemState(data.UpdateType))
        {
            switch (home)
            {
                case WireHome.World:
                case WireHome.TrainInterior:
                case WireHome.Shelf:
                    Vector3Serializer.Serialize(writer, data.ItemPosition);
                    QuaternionSerializer.Serialize(writer, data.ItemRotation);
                    if (home == WireHome.TrainInterior)
                        writer.Put(data.WorldParentNetId);
                    else if (home == WireHome.Shelf)
                        writer.Put(data.WorldParentKey ?? string.Empty);
                    if (home != WireHome.World)
                    {
                        Vector3Serializer.Serialize(writer, data.ParentLocalPosition);
                        QuaternionSerializer.Serialize(writer, data.ParentLocalRotation);
                    }
                    writer.Put(data.ItemState == ItemState.Thrown);
                    if (data.ItemState == ItemState.Thrown)
                        Vector3Serializer.Serialize(writer, data.ThrowDirection);
                    break;
                case WireHome.Hand:
                    writer.Put(data.PlayerId);
                    writer.Put((byte)data.PlayerHand);
                    break;
                case WireHome.Slot:
                    writer.Put(data.PlayerId);
                    writer.Put((sbyte)Math.Max(-1, Math.Min(sbyte.MaxValue, data.InventorySlot)));
                    break;
                case WireHome.Attached:
                    writer.Put(data.CarNetId);
                    writer.Put(data.AttachedFront);
                    break;
                case WireHome.Snapped:
                    writer.Put(data.WorldParentNetId);
                    // The key (the gadget item's id) stays on the server, which makes it from the NetId (OI-ID-07).
                    break;
                case WireHome.Installed:
                    writer.Put(data.CarNetId);
                    break;
                case WireHome.Magazine:
                    writer.Put(data.MagazineNetId);
                    writer.Put((sbyte)Math.Max(-1, Math.Min(sbyte.MaxValue, data.MagazineSlot)));
                    break;
            }
        }

        if (data.UpdateType.HasFlag(ItemUpdateType.Create) || data.UpdateType.HasFlag(ItemUpdateType.ObjectState))
            SerializeStates(writer, data.States);
    }

    public static ItemUpdate Deserialize(NetDataReader reader)
    {
        ItemUpdate data = new();

        data.UpdateType = (ItemUpdateType)reader.GetByte();
        data.ItemNetId = reader.GetUShort();
        data.AuthorityRevision = reader.GetUInt();
        data.Relation = (ItemRelation)reader.GetByte();
        if (reader.GetBool())
        {
            data.InventoryClaimSlot = reader.GetInt();
            data.InventoryClaimFlags = (ItemInventoryClaimFlags)reader.GetByte();
        }
        data.TransitionReason = (ItemTransitionReason)reader.GetByte();
        data.OriginatingPlayerId = reader.GetByte();

        if (data.UpdateType == ItemUpdateType.Destroy)
        {
            data.RemovalCause = DeserializeCause<ItemRemovalReason>(reader);
            return data;
        }
        if (IncludesItemState(data.UpdateType))
            data.MoveCause = DeserializeCause<ItemMoveReason>(reader);
        WireHome home = (WireHome)reader.GetByte();
        data.ItemState = StateOf(home);
        data.WorldParentKind = home switch
        {
            WireHome.TrainInterior => ItemWorldParentKind.TrainInterior,
            WireHome.Shelf => ItemWorldParentKind.StaticParent,
            WireHome.Snapped => ItemWorldParentKind.SnapPoint,
            _ => ItemWorldParentKind.World
        };

        if (data.UpdateType.HasFlag(ItemUpdateType.Create))
        {
            data.PrefabName = reader.GetString();
            data.AuthoredItemKey = reader.GetString();
            data.BelongsToPlayer = reader.GetBool();
        }
        if (IncludesItemState(data.UpdateType))
        {
            switch (home)
            {
                case WireHome.World:
                case WireHome.TrainInterior:
                case WireHome.Shelf:
                    data.ItemPosition = Vector3Serializer.Deserialize(reader);
                    data.ItemRotation = QuaternionSerializer.Deserialize(reader);
                    if (home == WireHome.TrainInterior)
                        data.WorldParentNetId = reader.GetUShort();
                    else if (home == WireHome.Shelf)
                        data.WorldParentKey = reader.GetString();
                    if (home != WireHome.World)
                    {
                        data.ParentLocalPosition = Vector3Serializer.Deserialize(reader);
                        data.ParentLocalRotation = QuaternionSerializer.Deserialize(reader);
                    }
                    if (reader.GetBool())
                    {
                        data.ItemState = ItemState.Thrown;
                        data.ThrowDirection = Vector3Serializer.Deserialize(reader);
                    }
                    break;
                case WireHome.Hand:
                    data.PlayerId = reader.GetByte();
                    data.PlayerHand = (PlayerHand)reader.GetByte();
                    break;
                case WireHome.Slot:
                    data.PlayerId = reader.GetByte();
                    data.InventorySlot = reader.GetSByte();
                    break;
                case WireHome.Attached:
                    data.CarNetId = reader.GetUShort();
                    data.AttachedFront = reader.GetBool();
                    break;
                case WireHome.Snapped:
                    data.WorldParentNetId = reader.GetUShort();
                    break;
                case WireHome.Installed:
                    data.CarNetId = reader.GetUShort();
                    break;
                case WireHome.Magazine:
                    data.MagazineNetId = reader.GetUShort();
                    data.MagazineSlot = reader.GetSByte();
                    break;
            }
        }

        if (data.UpdateType.HasFlag(ItemUpdateType.Create) || data.UpdateType.HasFlag(ItemUpdateType.ObjectState))
            data.States = DeserializeStates(reader);

        return data;
    }

    public bool HasClaim => InventoryClaimSlot >= 0 || InventoryClaimFlags != ItemInventoryClaimFlags.None;

    /// <summary>
    /// The home an update carries, as the wire writes it (docs/design/items/wire.md, "The update"):
    /// the ledger's home kinds that a live item can have. Values are on the wire: append only.
    /// </summary>
    public enum WireHome : byte
    {
        World,
        TrainInterior,
        Shelf,
        Hand,
        Slot,
        Attached,
        /// <summary>Snapped to a parent (a hanger, a mount): a gadget's snap point, by the gadget item's NetId (the server keeps the key).</summary>
        Snapped,
        /// <summary>A gadget on a car, or on the world (car 0).</summary>
        Installed,
        /// <summary>Ammo in a tool's magazine, by the tool's NetId and the slot.</summary>
        Magazine
    }

    public static WireHome HomeOf(ItemUpdate data) => data.ItemState switch
    {
        ItemState.InHand => WireHome.Hand,
        ItemState.InInventory => WireHome.Slot,
        ItemState.Attached => data.WorldParentKind == ItemWorldParentKind.SnapPoint ? WireHome.Snapped : WireHome.Attached,
        ItemState.Installed => WireHome.Installed,
        ItemState.InMagazine => WireHome.Magazine,
        _ => data.WorldParentKind switch
        {
            ItemWorldParentKind.TrainInterior => WireHome.TrainInterior,
            ItemWorldParentKind.StaticParent => WireHome.Shelf,
            _ => WireHome.World
        }
    };

    private static ItemState StateOf(WireHome home) => home switch
    {
        WireHome.Hand => ItemState.InHand,
        WireHome.Slot => ItemState.InInventory,
        WireHome.Attached or WireHome.Snapped => ItemState.Attached,
        WireHome.Installed => ItemState.Installed,
        WireHome.Magazine => ItemState.InMagazine,
        _ => ItemState.Dropped
    };

    /// <summary>
    /// A copy for one recipient: its relation, and the claim only if it's the claimant
    /// (<c>NetworkServer.SendItemUpdates</c>). The object state is shared, not copied.
    /// </summary>
    public ItemUpdate For(ItemRelation relation, bool claimant)
    {
        ItemUpdate copy = (ItemUpdate)MemberwiseClone();
        copy.Relation = relation;
        if (!claimant)
        {
            copy.InventoryClaimSlot = -1;
            copy.InventoryClaimFlags = ItemInventoryClaimFlags.None;
        }
        return copy;
    }

    private static void SerializeCause<TReason>(NetDataWriter writer, ChangeCause<TReason>? cause)
        where TReason : struct, Enum
    {
        writer.Put(cause.HasValue);
        if (cause.HasValue)
            ChangeCauseData.Serialize(writer, cause.Value);
    }

    private static ChangeCause<TReason>? DeserializeCause<TReason>(NetDataReader reader)
        where TReason : struct, Enum =>
        reader.GetBool() ? ChangeCauseData.Deserialize<TReason>(reader) : null;

    /// <summary>An item's object state (its tracked values) on the wire: an update's, or a request's.</summary>
    public static void SerializeStates(NetDataWriter writer, Dictionary<string, object> states)
    {
        if (states == null)
        {
            writer.Put(0);
            return;
        }
        writer.Put(states.Count);
        foreach (var state in states)
        {
            writer.Put(state.Key);
            SerializeTrackedValue(writer, state.Key, state.Value);
        }
    }

    public static Dictionary<string, object> DeserializeStates(NetDataReader reader)
    {
        int stateCount = reader.GetInt();
        if (stateCount <= 0)
            return null;
        Dictionary<string, object> states = new();
        for (int i = 0; i < stateCount; i++)
        {
            string key = reader.GetString();
            states[key] = DeserializeTrackedValue(reader, key);
        }
        return states;
    }

    private static void SerializeTrackedValue(NetDataWriter writer, string key, object value)
    {
        if (value is bool boolValue)
        {
            writer.Put((byte)0);
            writer.Put(boolValue);
        }
        else if (value is int intValue)
        {
            writer.Put((byte)1);
            writer.Put(intValue);
        }
        else if (value is uint uintValue)
        {
            writer.Put((byte)2);
            writer.Put(uintValue);
        }
        else if (value is float floatValue)
        {
            writer.Put((byte)3);
            writer.Put(floatValue);
        }
        else if (value is string stringValue)
        {
            writer.Put((byte)4);
            writer.Put(stringValue);
        }
        else
        {
            throw new NotSupportedException($"ItemUpdate.SerializeTrackedValue({key}) Unsupported type for serialization: {value?.GetType()}");
        }
    }

    private static object DeserializeTrackedValue(NetDataReader reader, string key)
    {
        byte typeCode = reader.GetByte();
        switch (typeCode)
        {
            case 0: return reader.GetBool();
            case 1: return reader.GetInt();
            case 2: return reader.GetUInt();
            case 3: return reader.GetFloat();
            case 4: return reader.GetString();

            default:
                throw new NotSupportedException($"ItemUpdate.DeserializeTrackedValue({key}) Unsupported type code for deserialization: {typeCode}");
        }
    }
}
