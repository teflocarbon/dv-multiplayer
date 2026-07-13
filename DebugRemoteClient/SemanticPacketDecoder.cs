using LiteNetLib.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Networking.Serialization;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.DebugClient;

/// <summary>
/// Debug-only schemas for selected high-value packets. These deliberately read the production
/// wire order without resolving any game object or Unity singleton.
/// </summary>
internal static class SemanticPacketDecoder
{
    private static readonly ulong commonItemChangeHash = GetHash(typeof(CommonItemChangePacket));
    private static bool enabled;

    public static void Enable() => enabled = true;

    public static bool TryDecode(byte[] raw, out SemanticPacket packet)
    {
        packet = null;
        if (!enabled || raw == null || raw.Length < sizeof(ulong) || BitConverter.ToUInt64(raw, 0) != commonItemChangeHash)
            return false;

        try
        {
            NetDataReader reader = new(raw);
            reader.GetULong(); // packet hash, already matched above
            packet = DecodeCommonItemChange(reader);
            if (reader.AvailableBytes != 0)
                throw new InvalidOperationException($"CommonItemChangePacket has {reader.AvailableBytes} unread byte(s).");
            return true;
        }
        catch (Exception exception)
        {
            packet = new SemanticDecodeFailure("CommonItemChangePacket", exception);
            return true;
        }
    }

    private static DebugCommonItemChangePacket DecodeCommonItemChange(NetDataReader reader)
    {
        bool compressed = reader.GetBool();
        int itemCount = reader.GetInt();
        NetDataReader itemReader = reader;
        if (compressed)
            itemReader = new NetDataReader(PacketCompression.Decompress(reader.GetBytesWithLength()));

        DebugCommonItemChangePacket result = new() { Compressed = compressed, DeclaredItemCount = itemCount };
        for (int index = 0; index < itemCount; index++)
            result.Items.Add(DecodeItem(itemReader));

        // Production CommonItemChangePacket.SerializeCompressed() passes NetDataWriter.Data
        // to the compressor. Data is the backing buffer and can contain unused capacity after
        // the declared item stream. The production deserializer intentionally ignores it.
        if (compressed)
            result.TrailingBufferBytes = itemReader.AvailableBytes;
        return result;
    }

    private static DebugItemUpdate DecodeItem(NetDataReader reader)
    {
        DebugItemUpdate item = new()
        {
            UpdateType = (ItemUpdateData.ItemUpdateType)reader.GetByte(),
            ItemNetId = reader.GetUShort()
        };
        item.UpdateTypeFlags = ProtocolEnumNames.Format(typeof(ItemUpdateData.ItemUpdateType), (long)item.UpdateType, flags: true);

        if (item.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
            return item;

        ItemState itemState = (ItemState)reader.GetByte();
        item.ItemState = itemState;
        item.ItemStateName = ProtocolEnumNames.Format(typeof(ItemState), (long)itemState, flags: false);
        if (item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
            item.PrefabName = reader.GetString();

        if (item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ItemState))
        {
            switch (itemState)
            {
                case ItemState.Dropped:
                case ItemState.Thrown:
                    item.Position = ReadVector3(reader);
                    item.Rotation = ReadQuaternion(reader);
                    if (item.ItemState == ItemState.Thrown)
                        item.ThrowDirection = ReadVector3(reader);
                    break;
                case ItemState.InHand:
                case ItemState.InInventory:
                    item.Player = reader.GetByte();
                    break;
                case ItemState.Attached:
                    item.CarNetId = reader.GetUShort();
                    item.AttachedFront = reader.GetBool();
                    break;
            }
        }

        if (item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState))
        {
            int stateCount = reader.GetInt();
            item.States = new Dictionary<string, object>();
            for (int index = 0; index < stateCount; index++)
                item.States[reader.GetString()] = ReadTrackedValue(reader);
        }

        return item;
    }

    private static DebugVector3 ReadVector3(NetDataReader reader) => new() { X = reader.GetFloat(), Y = reader.GetFloat(), Z = reader.GetFloat() };
    private static DebugQuaternion ReadQuaternion(NetDataReader reader) => new() { X = reader.GetFloat(), Y = reader.GetFloat(), Z = reader.GetFloat(), W = reader.GetFloat() };

    private static object ReadTrackedValue(NetDataReader reader) => reader.GetByte() switch
    {
        0 => reader.GetBool(),
        1 => reader.GetInt(),
        2 => reader.GetUInt(),
        3 => reader.GetFloat(),
        4 => reader.GetString(),
        byte code => throw new NotSupportedException($"Unsupported item tracked-value type code {code}.")
    };

    public static void RunProductionRoundTripTest()
    {
        bool previousEnabled = enabled;
        enabled = true;
        try
        {
        CommonItemChangePacket source = new()
        {
            Items = new List<ItemUpdateData>
            {
                new() { UpdateType = ItemUpdateData.ItemUpdateType.ItemState, ItemNetId = 392, ItemState = ItemState.InInventory, Player = 2 },
                new()
                {
                    UpdateType = ItemUpdateData.ItemUpdateType.Create | ItemUpdateData.ItemUpdateType.ObjectState,
                    ItemNetId = 77,
                    ItemState = ItemState.Thrown,
                    PrefabName = "Flashlight",
                    ItemPosition = new Vector3(1, 2, 3),
                    ItemRotation = new Quaternion(0, 0.5f, 0, 1),
                    ThrowDirection = new Vector3(4, 5, 6),
                    States = new Dictionary<string, object> { ["battery"] = 93.5f, ["enabled"] = true }
                }
            }
        };

        NetDataWriter writer = new();
        writer.Put(commonItemChangeHash);
        source.Serialize(writer); // Production serializer is the schema authority for this test.
        byte[] raw = new byte[writer.Length];
        Buffer.BlockCopy(writer.Data, 0, raw, 0, raw.Length);

        if (!TryDecode(raw, out SemanticPacket decoded) || decoded is not DebugCommonItemChangePacket items || items.Items.Count != 2)
            throw new InvalidOperationException("Semantic CommonItemChangePacket round-trip did not decode two items.");

        DebugItemUpdate inventory = items.Items[0];
        DebugItemUpdate thrown = items.Items[1];
        if (inventory.ItemNetId != 392 || inventory.ItemState != ItemState.InInventory || inventory.Player != 2 ||
            thrown.ItemNetId != 77 || thrown.ItemState != ItemState.Thrown || thrown.Position.X != 1 || thrown.ThrowDirection.Z != 6 ||
            thrown.States["battery"] is not float battery || battery != 93.5f || thrown.States["enabled"] is not bool enabledValue || !enabledValue)
            throw new InvalidOperationException("Semantic CommonItemChangePacket round-trip field equality failed.");

        CommonItemChangePacket compressedSource = new()
        {
            Items = Enumerable.Range(0, 51).Select(index => new ItemUpdateData
            {
                UpdateType = ItemUpdateData.ItemUpdateType.ItemState,
                ItemNetId = (ushort)(500 + index),
                ItemState = ItemState.InInventory,
                Player = 2
            }).ToList()
        };
        writer.Reset();
        writer.Put(commonItemChangeHash);
        compressedSource.Serialize(writer);
        raw = new byte[writer.Length];
        Buffer.BlockCopy(writer.Data, 0, raw, 0, raw.Length);
        if (!TryDecode(raw, out decoded) || decoded is not DebugCommonItemChangePacket compressed || !compressed.Compressed || compressed.Items.Count != 51)
            throw new InvalidOperationException("Semantic CommonItemChangePacket compressed round-trip failed.");
        }
        finally { enabled = previousEnabled; }
    }

    private static ulong GetHash(Type type)
    {
        MethodInfo method = typeof(NetPacketProcessor).GetMethod("GetHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return (ulong)method.MakeGenericMethod(type).Invoke(new NetPacketProcessor(), null);
    }
}

internal abstract class SemanticPacket
{
    public abstract string PacketType { get; }
    public virtual string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
}

internal sealed class DebugCommonItemChangePacket : SemanticPacket
{
    public override string PacketType => nameof(CommonItemChangePacket);
    public bool Compressed { get; set; }
    public int DeclaredItemCount { get; set; }
    public int TrailingBufferBytes { get; set; }
    public List<DebugItemUpdate> Items { get; } = [];
}

internal sealed class DebugItemUpdate
{
    public ItemUpdateData.ItemUpdateType UpdateType { get; set; }
    public string UpdateTypeFlags { get; set; }
    public ushort ItemNetId { get; set; }
    public ItemState? ItemState { get; set; }
    public string ItemStateName { get; set; }
    public string PrefabName { get; set; }
    public byte? Player { get; set; }
    public DebugVector3 Position { get; set; }
    public DebugQuaternion Rotation { get; set; }
    public DebugVector3 ThrowDirection { get; set; }
    public ushort? CarNetId { get; set; }
    public bool? AttachedFront { get; set; }
    public Dictionary<string, object> States { get; set; }
}

internal sealed class DebugVector3 { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } }
internal sealed class DebugQuaternion { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } public float W { get; set; } }

/// <summary>Renders wire enum values from the verified build manifest, with a harmless
/// reflection fallback only while inspecting an older manifest that predates a given enum.</summary>
internal static class ProtocolEnumNames
{
    public static string Format(Type enumType, long value, bool flags)
    {
        Dictionary<string, long> values = null;
        ProtocolManifestProvider.Current.Enums?.TryGetValue(enumType.FullName, out values);
        if (values != null)
        {
            string exact = values.FirstOrDefault(pair => pair.Value == value).Key;
            if (!string.IsNullOrEmpty(exact)) return exact;
            if (flags)
            {
                string[] parts = values.Where(pair => pair.Value != 0 && IsSingleBit(pair.Value) && (value & pair.Value) == pair.Value)
                    .OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
                if (parts.Length > 0) return string.Join(" | ", parts);
            }
            return $"Unknown({value})";
        }
        return Enum.IsDefined(enumType, value) ? Enum.GetName(enumType, value) : $"Unknown({value})";
    }

    private static bool IsSingleBit(long value) => value > 0 && (value & (value - 1)) == 0;
}

internal sealed class SemanticDecodeFailure : SemanticPacket
{
    private readonly string packetType;
    private readonly Exception exception;
    public SemanticDecodeFailure(string packetType, Exception exception) { this.packetType = packetType; this.exception = exception; }
    public override string PacketType => packetType;
    public override string ToJson() => JsonConvert.SerializeObject(new { Error = exception.Message, Exception = exception.ToString() }, Formatting.Indented);
}
