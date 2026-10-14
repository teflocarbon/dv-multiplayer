using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging;

public interface IDebugPacketProjector
{
    Type PacketType { get; }
    DebugPacketProjection Project(object packet, byte? localPlayerId);
}

public sealed class DebugPacketProjection
{
    public object Wire { get; set; }
    public Dictionary<string, object> Context { get; set; } = new(StringComparer.Ordinal);
    public string Summary { get; set; } = string.Empty;
}

public static class DebugPacketProjectorRegistry
{
    private static readonly Dictionary<Type, Func<object, byte?, DebugPacketProjection>> projectors = new();

    static DebugPacketProjectorRegistry()
    {
        Register<CommonItemUpdatePacket>((packet, localPlayerId) =>
        {
            Dictionary<string, object> item = ProjectItemUpdate(packet?.ItemData);
            return new DebugPacketProjection
            {
                Wire = new Dictionary<string, object> { ["packetType"] = nameof(CommonItemUpdatePacket), ["itemData"] = item },
                Context = ItemContext(packet?.ItemData, localPlayerId), Summary = SummarizeItem(packet?.ItemData)
            };
        });
        Register<CommonItemsBulkUpdatePacket>((packet, localPlayerId) =>
        {
            List<Dictionary<string, object>> items = packet?.Items?.Select(ProjectItemUpdate).ToList() ?? new();
            return new DebugPacketProjection
            {
                Wire = new Dictionary<string, object> { ["packetType"] = nameof(CommonItemsBulkUpdatePacket), ["count"] = items.Count, ["items"] = items },
                Context = localPlayerId.HasValue ? new Dictionary<string, object> { ["localPlayerId"] = localPlayerId.Value } : new(),
                Summary = $"{items.Count} item update{(items.Count == 1 ? string.Empty : "s")}"
            };
        });
        Register<ItemUpdateData>((item, localPlayerId) => new DebugPacketProjection
        {
            Wire = ProjectItemUpdate(item), Context = ItemContext(item, localPlayerId), Summary = SummarizeItem(item)
        });
    }

    public static void Register<T>(Func<T, byte?, DebugPacketProjection> projector) => projectors[typeof(T)] = (value, player) => projector((T)value, player);

    public static DebugPacketProjection Project(object packet, byte? localPlayerId = null)
    {
        if (packet == null) return new DebugPacketProjection { Wire = null, Summary = "null packet" };
        if (projectors.TryGetValue(packet.GetType(), out Func<object, byte?, DebugPacketProjection> projector)) return projector(packet, localPlayerId);
        return new DebugPacketProjection
        {
            Wire = new Dictionary<string, object> { ["packetType"] = packet.GetType().Name, ["fields"] = DebugStrictSnapshotter.Snapshot(packet) },
            Summary = packet.GetType().Name
        };
    }

    public static Dictionary<string, object> ProjectItemUpdate(ItemUpdateData item)
    {
        if (item == null) return new Dictionary<string, object>(StringComparer.Ordinal) { ["value"] = null };
        Dictionary<string, object> result = new(StringComparer.Ordinal)
        {
            ["updateType"] = item.UpdateType.ToString(), ["itemNetId"] = item.ItemNetId
        };
        if (item.UpdateType == ItemUpdateData.ItemUpdateType.Destroy) return result;
        result["itemState"] = item.ItemState.ToString();
        if (item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create)) result["prefabName"] = item.PrefabName;
        if (item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ItemState))
        {
            switch (item.ItemState)
            {
                case ItemState.Dropped:
                    result["position"] = Vec3(item.ItemPosition); result["rotation"] = Quat(item.ItemRotation); break;
                case ItemState.Thrown:
                    result["position"] = Vec3(item.ItemPosition); result["rotation"] = Quat(item.ItemRotation); result["throwDirection"] = Vec3(item.ThrowDirection); break;
                case ItemState.InHand:
                case ItemState.InInventory:
                    result["playerId"] = item.PlayerId; break;
                case ItemState.Attached:
                    result["carNetId"] = item.CarNetId; result["attachedFront"] = item.AttachedFront; break;
            }
        }
        if (item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState))
            result["states"] = ProjectStates(item.States);
        return result;
    }

    private static Dictionary<string, object> ProjectStates(Dictionary<string, object> states)
    {
        Dictionary<string, object> result = new(StringComparer.Ordinal);
        if (states == null) return result;
        foreach (var pair in states.Take(256)) result[pair.Key] = pair.Value is string text && text.Length > 2048 ? text.Substring(0, 2048) + "…" : pair.Value;
        if (states.Count > 256) { result["_truncated"] = true; result["_originalCount"] = states.Count; }
        return result;
    }

    private static Dictionary<string, object> ItemContext(ItemUpdateData item, byte? localPlayerId)
    {
        Dictionary<string, object> context = new(StringComparer.Ordinal);
        if (item != null && !item.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) && !string.IsNullOrEmpty(item.PrefabName)) context["prefabName"] = item.PrefabName;
        if (localPlayerId.HasValue) context["localPlayerId"] = localPlayerId.Value;
        return context;
    }

    private static string SummarizeItem(ItemUpdateData item)
    {
        if (item == null) return "null item update";
        string prefix = $"Item {item.ItemNetId} {item.UpdateType}";
        if (item.UpdateType == ItemUpdateData.ItemUpdateType.Destroy) return prefix;
        return item.ItemState switch
        {
            ItemState.Dropped => $"{prefix} Dropped pos={VectorText(item.ItemPosition)}",
            ItemState.Thrown => $"{prefix} Thrown pos={VectorText(item.ItemPosition)} dir={VectorText(item.ThrowDirection)}",
            ItemState.InHand => $"{prefix} InHand player={item.PlayerId}",
            ItemState.InInventory => $"{prefix} InInventory player={item.PlayerId}",
            ItemState.Attached => $"{prefix} Attached car={item.CarNetId} front={item.AttachedFront}",
            _ => $"{prefix} {item.ItemState}"
        };
    }

    private static Dictionary<string, object> Vec3(Vector3 value) => new(StringComparer.Ordinal) { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
    private static Dictionary<string, object> Quat(Quaternion value) => new(StringComparer.Ordinal) { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z, ["w"] = value.w };
    private static string VectorText(Vector3 value) => $"({value.x:0.00},{value.y:0.00},{value.z:0.00})";
}
