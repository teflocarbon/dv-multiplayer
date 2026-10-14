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
/// Immutable, raw-datagram item schemas for this branch. CommonItemUpdatePacket and
/// CommonItemsBulkUpdatePacket use the registered ItemUpdateData nested serializer; decoding
/// from the raw payload prevents a reusable production packet instance from being observed
/// after it has been mutated for a later datagram.
/// </summary>
internal static class SemanticPacketDecoder
{
    private const string SingleDecoderId = "CommonItemUpdateV1";
    private const string BulkDecoderId = "CommonItemsBulkUpdateV1";
    private static readonly ulong singleItemHash = GetHash(typeof(CommonItemUpdatePacket));
    private static readonly ulong bulkItemsHash = GetHash(typeof(CommonItemsBulkUpdatePacket));
    private static bool enabled;

    public static void Enable() => enabled = true;

    public static bool TryDecode(byte[] raw, out SemanticPacket packet)
    {
        packet = null;
        if (!enabled || raw == null || raw.Length < sizeof(ulong)) return false;

        ulong hash = BitConverter.ToUInt64(raw, 0);
        string decoderId = hash == singleItemHash ? SingleDecoderId : hash == bulkItemsHash ? BulkDecoderId : null;
        if (decoderId == null || !ManifestDeclares(hash, decoderId)) return false;

        try
        {
            NetDataReader reader = new(raw);
            reader.GetULong();
            packet = hash == singleItemHash
                ? new DebugCommonItemUpdatePacket { Item = DecodeItem(reader) }
                : DecodeBulkPacket(reader);
            if (reader.AvailableBytes != 0)
                throw new InvalidOperationException($"{packet.PacketType} has {reader.AvailableBytes} unread byte(s).");
            return true;
        }
        catch (Exception exception)
        {
            packet = new SemanticDecodeFailure(hash == singleItemHash ? nameof(CommonItemUpdatePacket) : nameof(CommonItemsBulkUpdatePacket), exception);
            return true;
        }
    }

    private static DebugCommonItemsBulkUpdatePacket DecodeBulkPacket(NetDataReader reader)
    {
        // LiteNetLib's reusable serializer writes List<T> length as UInt16 for this packet.
        ushort count = reader.GetUShort();
        DebugCommonItemsBulkUpdatePacket result = new() { DeclaredItemCount = count };
        for (int index = 0; index < count; index++) result.Items.Add(DecodeItem(reader));
        return result;
    }

    private static DebugItemUpdate DecodeItem(NetDataReader reader)
    {
        DebugItemUpdate item = new()
        {
            UpdateType = reader.GetByte(),
            ItemNetId = reader.GetUShort()
        };
        ItemUpdateData.ItemUpdateType flags = (ItemUpdateData.ItemUpdateType)item.UpdateType;
        item.UpdateTypeFlags = ProtocolEnumNames.Format(typeof(ItemUpdateData.ItemUpdateType), item.UpdateType, flags: true);
        if (flags == ItemUpdateData.ItemUpdateType.Destroy) return item;

        item.ItemState = reader.GetByte();
        ItemState state = (ItemState)item.ItemState;
        item.ItemStateName = ProtocolEnumNames.Format(typeof(ItemState), item.ItemState.Value, flags: false);
        if (flags.HasFlag(ItemUpdateData.ItemUpdateType.Create)) item.PrefabName = reader.GetString();

        if (flags.HasFlag(ItemUpdateData.ItemUpdateType.Create) || flags.HasFlag(ItemUpdateData.ItemUpdateType.ItemState))
        {
            switch (state)
            {
                case ItemState.Dropped:
                case ItemState.Thrown:
                    item.Position = ReadVector3(reader);
                    item.Rotation = ReadQuaternion(reader);
                    if (state == ItemState.Thrown) item.ThrowDirection = ReadVector3(reader);
                    break;
                case ItemState.InHand:
                case ItemState.InInventory:
                    item.PlayerId = reader.GetByte();
                    break;
                case ItemState.Attached:
                    item.CarNetId = reader.GetUShort();
                    item.AttachedFront = reader.GetBool();
                    break;
            }
        }

        if (flags.HasFlag(ItemUpdateData.ItemUpdateType.Create) || flags.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState))
        {
            int stateCount = reader.GetInt();
            item.States = new Dictionary<string, object>();
            for (int index = 0; index < stateCount; index++) item.States[reader.GetString()] = ReadTrackedValue(reader);
        }
        return item;
    }

    private static DebugVector3 ReadVector3(NetDataReader reader) => new() { X = reader.GetFloat(), Y = reader.GetFloat(), Z = reader.GetFloat() };
    private static DebugQuaternion ReadQuaternion(NetDataReader reader) => new() { X = reader.GetFloat(), Y = reader.GetFloat(), Z = reader.GetFloat(), W = reader.GetFloat() };
    private static object ReadTrackedValue(NetDataReader reader) => reader.GetByte() switch
    {
        0 => reader.GetBool(), 1 => reader.GetInt(), 2 => reader.GetUInt(), 3 => reader.GetFloat(), 4 => reader.GetString(),
        byte code => throw new NotSupportedException($"Unsupported item tracked-value type code {code}.")
    };

    private static bool ManifestDeclares(ulong hash, string decoderId) => ProtocolManifestProvider.Current.Packets.Any(packet =>
        string.Equals(packet.Hash, hash.ToString("X16"), StringComparison.OrdinalIgnoreCase) && packet.SemanticDecoder == decoderId);

    public static void RunProductionRoundTripTest()
    {
        bool previousEnabled = enabled;
        enabled = true;
        try
        {
            NetPacketProcessor processor = new();
            PacketSerializationRegistry.Register(processor);
            ItemUpdateData thrown = new()
            {
                UpdateType = ItemUpdateData.ItemUpdateType.FullSync, ItemNetId = 390, ItemState = ItemState.Thrown,
                ItemPosition = new Vector3(1, 2, 3), ItemRotation = new Quaternion(0, .5f, 0, 1), ThrowDirection = new Vector3(4, 5, 6), States = new()
            };
            NetDataWriter writer = new();
            processor.Write(writer, new CommonItemUpdatePacket { ItemData = thrown });
            AssertThrown(writer, "CommonItemUpdatePacket");

            writer.Reset();
            processor.Write(writer, new CommonItemsBulkUpdatePacket { Items = new List<ItemUpdateData> { thrown } });
            AssertThrown(writer, "CommonItemsBulkUpdatePacket");
        }
        finally { enabled = previousEnabled; }
    }

    private static void AssertThrown(NetDataWriter writer, string packetType)
    {
        byte[] raw = new byte[writer.Length];
        Buffer.BlockCopy(writer.Data, 0, raw, 0, raw.Length);
        if (!TryDecode(raw, out SemanticPacket decoded)) throw new InvalidOperationException($"{packetType} was not selected by its manifest decoder.");
        DebugItemUpdate item = decoded switch
        {
            DebugCommonItemUpdatePacket single => single.Item,
            DebugCommonItemsBulkUpdatePacket bulk when bulk.Items.Count == 1 => bulk.Items[0],
            _ => null
        };
        if (item?.ItemState != (byte)ItemState.Thrown || item.Position?.X != 1 || item.ThrowDirection?.Z != 6 || item.States?.Count != 0)
            throw new InvalidOperationException($"{packetType} raw semantic round-trip failed.");
    }

    // Production formatter fallback is deliberately disabled. Once the manifest declares a raw
    // item schema, the immutable raw DTO is authoritative over reusable packet objects.
    public static bool TryFormatProductionPacket(object packet, out string detail) { detail = null; return false; }
    public const string Status = "Raw semantic schemas enabled for CommonItemUpdatePacket and CommonItemsBulkUpdatePacket.";

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

internal sealed class DebugCommonItemUpdatePacket : SemanticPacket
{
    public override string PacketType => nameof(CommonItemUpdatePacket);
    public DebugItemUpdate Item { get; set; }
}

internal sealed class DebugCommonItemsBulkUpdatePacket : SemanticPacket
{
    public override string PacketType => nameof(CommonItemsBulkUpdatePacket);
    public int DeclaredItemCount { get; set; }
    public List<DebugItemUpdate> Items { get; } = [];
}

internal sealed class DebugItemUpdate
{
    public byte UpdateType { get; set; }
    public string UpdateTypeFlags { get; set; }
    public ushort ItemNetId { get; set; }
    public byte? ItemState { get; set; }
    public string ItemStateName { get; set; }
    public string PrefabName { get; set; }
    public byte? PlayerId { get; set; }
    public DebugVector3 Position { get; set; }
    public DebugQuaternion Rotation { get; set; }
    public DebugVector3 ThrowDirection { get; set; }
    public ushort? CarNetId { get; set; }
    public bool? AttachedFront { get; set; }
    public Dictionary<string, object> States { get; set; }
}
internal sealed class DebugVector3 { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } }
internal sealed class DebugQuaternion { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } public float W { get; set; } }

internal sealed class SemanticDecodeFailure : SemanticPacket
{
    private readonly string packetType; private readonly Exception exception;
    public SemanticDecodeFailure(string packetType, Exception exception) { this.packetType = packetType; this.exception = exception; }
    public override string PacketType => packetType;
    public override string ToJson() => JsonConvert.SerializeObject(new { Error = exception.Message, Exception = exception.ToString() }, Formatting.Indented);
}

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
                string[] parts = values.Where(pair => pair.Value != 0 && IsSingleBit(pair.Value) && (value & pair.Value) == pair.Value).OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
                if (parts.Length > 0) return string.Join(" | ", parts);
            }
        }
        return $"Unknown({value})";
    }
    private static bool IsSingleBit(long value) => value > 0 && (value & (value - 1)) == 0;
}
