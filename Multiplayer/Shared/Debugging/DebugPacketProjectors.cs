using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Protocol.Data.Jobs;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Jobs;
using Multiplayer.Shared.Protocol.Packets.Items;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Debugging;

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
        Register<ClientboundItemUpdatesPacket>((packet, localPlayerId) =>
        {
            List<Dictionary<string, object>> items = packet?.Updates?.Select(ProjectItemUpdate).ToList() ?? new();
            return new DebugPacketProjection
            {
                Wire = new Dictionary<string, object> { ["packetType"] = nameof(ClientboundItemUpdatesPacket), ["count"] = items.Count, ["items"] = items },
                Context = items.Count == 1 ? ItemContext(packet.Updates[0], localPlayerId) : PlayerContext(localPlayerId),
                Summary = items.Count == 1 ? SummarizeItem(packet.Updates[0]) : $"{items.Count} item updates"
            };
        });
        Register<ServerboundItemRequestPacket>((packet, localPlayerId) => new DebugPacketProjection
        {
            Wire = new Dictionary<string, object> { ["packetType"] = nameof(ServerboundItemRequestPacket), ["request"] = ProjectItemRequest(packet?.Request) },
            Context = PlayerContext(localPlayerId), Summary = SummarizeRequest(packet?.Request)
        });
        Register<ClientboundItemResultPacket>((packet, localPlayerId) => new DebugPacketProjection
        {
            Wire = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["packetType"] = nameof(ClientboundItemResultPacket),
                ["requestId"] = packet.RequestId,
                ["refusal"] = packet.Refusal.ToString(),
                ["detail"] = packet.Detail ?? string.Empty,
                ["revision"] = packet.Revision,
                ["itemNetId"] = packet.ItemNetId
            },
            Context = PlayerContext(localPlayerId),
            Summary = $"Request {packet.RequestId} " + (packet.Accepted
                ? $"accepted r{packet.Revision}" + (packet.ItemNetId == 0 ? string.Empty : $" item {packet.ItemNetId}")
                : $"refused {packet.Refusal} {packet.Detail}")
        });
        Register<ServerboundItemListRequestPacket>((packet, localPlayerId) => new DebugPacketProjection
        {
            Wire = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["packetType"] = nameof(ServerboundItemListRequestPacket),
                ["requestId"] = packet.RequestId,
                ["kind"] = packet.Kind.ToString(),
                ["shellNetId"] = packet.ShellNetId,
                ["container"] = packet.Container
            },
            Context = PlayerContext(localPlayerId),
            Summary = $"List {packet.RequestId} {packet.Kind}" +
                (packet.Kind == ItemListKind.Container ? $" shell={packet.ShellNetId} handle={packet.Container}" : string.Empty)
        });
        Register<ClientboundItemListPacket>((packet, localPlayerId) =>
        {
            List<Dictionary<string, object>> rows = packet.Rows?.Select(ProjectItemRow).ToList() ?? new();
            return new DebugPacketProjection
            {
                Wire = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["packetType"] = nameof(ClientboundItemListPacket),
                    ["requestId"] = packet.RequestId,
                    ["kind"] = packet.Kind.ToString(),
                    ["refusal"] = packet.Refusal.ToString(),
                    ["detail"] = packet.Detail ?? string.Empty,
                    ["container"] = packet.Container,
                    ["revision"] = packet.Revision,
                    ["capacity"] = packet.Capacity,
                    ["rows"] = rows
                },
                Context = PlayerContext(localPlayerId),
                Summary = $"List {packet.RequestId} {packet.Kind} " + (packet.Accepted
                    ? $"{rows.Count} row{(rows.Count == 1 ? string.Empty : "s")}"
                    : $"refused {packet.Refusal} {packet.Detail}")
            };
        });
        Register<ClientboundInventoryRowsPacket>((packet, localPlayerId) =>
        {
            List<Dictionary<string, object>> rows = packet?.Rows?.Select((row, i) =>
            {
                Dictionary<string, object> projected = ProjectItemUpdate(row);
                // The state is the item's own save JSON: its length, not its contents.
                projected["stateLength"] = packet.States != null && i < packet.States.Length ? packet.States[i]?.Length ?? 0 : 0;
                return projected;
            }).ToList() ?? new();
            return new DebugPacketProjection
            {
                Wire = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["packetType"] = nameof(ClientboundInventoryRowsPacket),
                    ["full"] = packet?.Full ?? false,
                    ["count"] = rows.Count,
                    ["rows"] = rows
                },
                Context = PlayerContext(localPlayerId),
                Summary = $"{rows.Count} inventory row{(rows.Count == 1 ? string.Empty : "s")}" + (packet?.Full == true ? " (whole inventory)" : string.Empty)
            };
        });
        Register<ItemUpdate>((item, localPlayerId) => new DebugPacketProjection
        {
            Wire = ProjectItemUpdate(item), Context = ItemContext(item, localPlayerId), Summary = SummarizeItem(item)
        });
        Register<ClientboundJobReportArtifactPacket>((packet, localPlayerId) =>
        {
            JobReportArtifactData artifact = JobReportArtifactData.Deserialize(packet.Payload);
            return new DebugPacketProjection
            {
                Wire = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["packetType"] = nameof(ClientboundJobReportArtifactPacket),
                    ["schemaVersion"] = artifact.SchemaVersion,
                    ["artifactToken"] = artifact.ArtifactToken,
                    ["itemNetId"] = artifact.ItemNetId,
                    ["jobNetId"] = artifact.JobNetId,
                    ["validationStationNetId"] = artifact.ValidationStationNetId,
                    ["position"] = DebugStrictSnapshotter.Snapshot(artifact.Position.Position),
                    ["rotation"] = DebugStrictSnapshotter.Snapshot(artifact.Position.Rotation),
                    ["job"] = DebugStrictSnapshotter.Snapshot(artifact.Job),
                    ["debt"] = DebugStrictSnapshotter.Snapshot(artifact.Debt)
                },
                Context = localPlayerId.HasValue
                    ? new Dictionary<string, object> { ["localPlayerId"] = localPlayerId.Value }
                    : new Dictionary<string, object>(),
                Summary = $"JobReport item {artifact.ItemNetId} job {artifact.Job?.Id ?? artifact.JobNetId.ToString()}" +
                          (artifact.Debt == null ? " no debt" : " with debt")
            };
        });
    }

    public static void Register<T>(Func<T, byte?, DebugPacketProjection> projector) => projectors[typeof(T)] = (value, player) => projector((T)value, player);

    public static DebugPacketProjection Project(object packet, byte? localPlayerId = null)
    {
        if (packet == null) return new DebugPacketProjection { Wire = null, Summary = "null packet" };
        if (projectors.TryGetValue(packet.GetType(), out Func<object, byte?, DebugPacketProjection> projector))
        {
            try
            {
                return projector(packet, localPlayerId);
            }
            catch (Exception exception)
            {
                return new DebugPacketProjection
                {
                    Wire = new Dictionary<string, object>
                    {
                        ["packetType"] = packet.GetType().Name,
                        ["projectionError"] = exception.Message
                    },
                    Summary = $"{packet.GetType().Name} (projection failed)"
                };
            }
        }
        return new DebugPacketProjection
        {
            Wire = new Dictionary<string, object> { ["packetType"] = packet.GetType().Name, ["fields"] = DebugStrictSnapshotter.Snapshot(packet) },
            Summary = packet.GetType().Name
        };
    }

    public static Dictionary<string, object> ProjectItemUpdate(ItemUpdate item)
    {
        if (item == null) return new Dictionary<string, object>(StringComparer.Ordinal) { ["value"] = null };
        Dictionary<string, object> result = new(StringComparer.Ordinal)
        {
            ["updateType"] = item.UpdateType.ToString(),
            ["itemNetId"] = item.ItemNetId,
            ["authorityRevision"] = item.AuthorityRevision,
            ["relation"] = item.Relation.ToString(),
            ["transitionReason"] = item.TransitionReason.ToString(),
            ["originatingPlayerId"] = item.OriginatingPlayerId
        };
        if (item.HasClaim)
            result["inventoryClaim"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["slot"] = item.InventoryClaimSlot,
                ["flags"] = item.InventoryClaimFlags.ToString()
            };
        if (item.RemovalCause.HasValue) result["removalCause"] = item.RemovalCause.Value.ToString();
        if (item.MoveCause.HasValue) result["moveCause"] = item.MoveCause.Value.ToString();
        if (item.UpdateType == ItemUpdate.ItemUpdateType.Destroy) return result;
        result["itemState"] = item.ItemState.ToString();
        if (item.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create)) result["prefabName"] = item.PrefabName;
        if (item.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create) || item.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.ItemState))
        {
            result["home"] = ItemUpdate.HomeOf(item).ToString();
            if (item.ItemState == ItemState.InInventory)
                result["slot"] = item.InventorySlot;
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
                case ItemState.Installed:
                    result["carNetId"] = item.CarNetId; break;
            }
        }
        if (item.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create) || item.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.ObjectState))
            result["states"] = ProjectStates(item.States);
        return result;
    }

    public static Dictionary<string, object> ProjectItemRequest(ItemRequest request)
    {
        if (request == null) return new Dictionary<string, object>(StringComparer.Ordinal) { ["value"] = null };
        Dictionary<string, object> result = new(StringComparer.Ordinal)
        {
            ["requestId"] = request.RequestId,
            ["action"] = request.Action.ToString(),
            ["itemNetId"] = request.ItemNetId,
            ["itemHandle"] = request.ItemHandle,
            ["expectedRevision"] = request.ExpectedRevision,
            ["slot"] = request.Slot
        };
        switch (request.Action)
        {
            case ItemAction.Take:
                result["hand"] = request.Hand.ToString(); break;
            case ItemAction.Stash:
                result["claimFlags"] = request.ClaimFlags.ToString(); break;
            case ItemAction.Drop:
                result["position"] = Vec3(request.Position); result["rotation"] = Quat(request.Rotation); break;
            case ItemAction.Throw:
                result["position"] = Vec3(request.Position); result["rotation"] = Quat(request.Rotation); result["throwDirection"] = Vec3(request.ThrowDirection); break;
            case ItemAction.Attach:
                result["carNetId"] = request.CarNetId; result["front"] = request.Front; break;
            case ItemAction.LoadMagazine:
                result["magazineNetId"] = request.MagazineNetId; result["magazineSlot"] = request.MagazineSlot; break;
            case ItemAction.UseUp:
                result["magazineNetId"] = request.MagazineNetId; break;
            case ItemAction.Deposit:
            case ItemAction.Withdraw:
            case ItemAction.MoveInContainer:
                result["container"] = request.Container; result["shellNetId"] = request.ShellNetId; result["containerRevision"] = request.ContainerRevision; break;
            case ItemAction.ReportBelowMap:
                result["surfaceHeight"] = request.SurfaceHeight; break;
        }
        if (request.WorldParentKind != ItemWorldParentKind.World)
        {
            result["worldParentKind"] = request.WorldParentKind.ToString();
            result["worldParentNetId"] = request.WorldParentNetId;
            result["worldParentKey"] = request.WorldParentKey ?? string.Empty;
        }
        if (request.States != null) result["states"] = ProjectStates(request.States);
        if (request.WorldInteraction) result["worldInteraction"] = true;
        return result;
    }

    private static Dictionary<string, object> ProjectItemRow(ItemRow row) => row == null
        ? new Dictionary<string, object>(StringComparer.Ordinal) { ["value"] = null }
        : new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["handle"] = row.Handle,
            ["prefabName"] = row.PrefabName ?? string.Empty,
            ["displayName"] = row.DisplayName ?? string.Empty,
            ["where"] = row.Where ?? string.Empty,
            ["flags"] = row.Flags.ToString(),
            ["slot"] = row.Slot,
            ["revision"] = row.Revision,
            ["claimSlot"] = row.ClaimSlot,
            ["itemNetId"] = row.ItemNetId
        };

    private static string SummarizeRequest(ItemRequest request) => request == null
        ? "null item request"
        : $"Request {request.RequestId} {request.Action} " +
          (request.ItemNetId != 0 ? $"item {request.ItemNetId}" : $"handle {request.ItemHandle}") +
          $" r{request.ExpectedRevision}" + (request.Slot >= 0 ? $" slot {request.Slot}" : string.Empty);

    private static Dictionary<string, object> PlayerContext(byte? localPlayerId) => localPlayerId.HasValue
        ? new Dictionary<string, object>(StringComparer.Ordinal) { ["localPlayerId"] = localPlayerId.Value }
        : new Dictionary<string, object>(StringComparer.Ordinal);

    private static Dictionary<string, object> ProjectStates(Dictionary<string, object> states)
    {
        Dictionary<string, object> result = new(StringComparer.Ordinal);
        if (states == null) return result;
        foreach (var pair in states.Take(256)) result[pair.Key] = pair.Value is string text && text.Length > 2048 ? text.Substring(0, 2048) + "…" : pair.Value;
        if (states.Count > 256) { result["_truncated"] = true; result["_originalCount"] = states.Count; }
        return result;
    }

    private static Dictionary<string, object> ItemContext(ItemUpdate item, byte? localPlayerId)
    {
        Dictionary<string, object> context = new(StringComparer.Ordinal);
        if (item != null && !item.UpdateType.HasFlag(ItemUpdate.ItemUpdateType.Create) && !string.IsNullOrEmpty(item.PrefabName)) context["prefabName"] = item.PrefabName;
        if (localPlayerId.HasValue) context["localPlayerId"] = localPlayerId.Value;
        return context;
    }

    private static string SummarizeItem(ItemUpdate item)
    {
        if (item == null) return "null item update";
        string prefix = $"Item {item.ItemNetId} r{item.AuthorityRevision} {item.Relation} {item.UpdateType}";
        if (item.UpdateType == ItemUpdate.ItemUpdateType.Destroy) return prefix;
        return item.ItemState switch
        {
            ItemState.Dropped => $"{prefix} Dropped pos={VectorText(item.ItemPosition)}",
            ItemState.Thrown => $"{prefix} Thrown pos={VectorText(item.ItemPosition)} dir={VectorText(item.ThrowDirection)}",
            ItemState.InHand => $"{prefix} InHand player={item.PlayerId}",
            ItemState.InInventory => $"{prefix} InInventory player={item.PlayerId}",
            ItemState.Attached => $"{prefix} Attached car={item.CarNetId} front={item.AttachedFront}",
            ItemState.Installed => $"{prefix} Installed car={item.CarNetId}",
            _ => $"{prefix} {item.ItemState}"
        };
    }

    private static Dictionary<string, object> Vec3(Vector3 value) => new(StringComparer.Ordinal) { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
    private static Dictionary<string, object> Quat(Quaternion value) => new(StringComparer.Ordinal) { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z, ["w"] = value.w };
    private static string VectorText(Vector3 value) => $"({value.x:0.00},{value.y:0.00},{value.z:0.00})";
}
