using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json.Linq;
using System;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

/// <summary>Typed representation of one entry in the version-one world-item save array.</summary>
internal sealed class WorldItemPersistenceRecord
{
    public Guid PersistentItemId { get; set; }
    public string AuthoredItemKey { get; set; } = string.Empty;
    public string PrefabName { get; set; } = string.Empty;
    public uint Revision { get; set; }
    public bool HasPersistentOverride { get; set; }
    public bool IsReplenishableStockFork { get; set; }
    public ItemPlacementKind Placement { get; set; } = ItemPlacementKind.World;
    public Guid OwnerIdentity { get; set; }
    public Guid PlacementPlayerIdentity { get; set; }
    public int ClaimSlot { get; set; } = -1;
    public ItemInventoryClaimFlags ClaimFlags { get; set; }
    public ItemWorldParentKind WorldParentKind { get; set; }
    public ushort WorldParentNetId { get; set; }
    public string WorldParentKey { get; set; } = string.Empty;
    public string WorldParentPersistentId { get; set; } = string.Empty;
    public string AttachedCarPersistentId { get; set; } = string.Empty;
    public bool AttachedFront { get; set; } = true;
    public Vector3 ParentLocalPosition { get; set; }
    public Quaternion ParentLocalRotation { get; set; } = Quaternion.identity;
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.identity;
    public JObject State { get; set; }

    public bool IsTombstone => Placement == ItemPlacementKind.Destroyed;

    public JObject ToJson()
    {
        if (IsTombstone)
            return new JObject
            {
                ["persistentItemId"] = string.Empty,
                ["authoredItemKey"] = AuthoredItemKey ?? string.Empty,
                ["prefabName"] = PrefabName ?? string.Empty,
                ["revision"] = Revision,
                ["placement"] = (byte)ItemPlacementKind.Destroyed
            };

        return new JObject
        {
            ["persistentItemId"] = FormatGuid(PersistentItemId),
            ["authoredItemKey"] = AuthoredItemKey ?? string.Empty,
            ["prefabName"] = PrefabName ?? string.Empty,
            ["revision"] = Revision,
            ["hasPersistentOverride"] = HasPersistentOverride,
            ["replenishableStockFork"] = IsReplenishableStockFork,
            ["placement"] = (byte)Placement,
            ["ownerIdentity"] = FormatGuid(OwnerIdentity),
            ["placementPlayerIdentity"] = FormatGuid(PlacementPlayerIdentity),
            ["claimSlot"] = ClaimSlot,
            ["claimFlags"] = (byte)ClaimFlags,
            ["worldParentKind"] = (byte)WorldParentKind,
            ["worldParentNetId"] = WorldParentNetId,
            ["worldParentKey"] = WorldParentKey ?? string.Empty,
            ["worldParentPersistentId"] = WorldParentPersistentId ?? string.Empty,
            ["attachedCarPersistentId"] = AttachedCarPersistentId ?? string.Empty,
            ["attachedFront"] = AttachedFront,
            ["parentLocalPosition"] = VectorToJson(ParentLocalPosition),
            ["parentLocalRotation"] = QuaternionToJson(ParentLocalRotation),
            ["position"] = VectorToJson(Position),
            ["rotation"] = QuaternionToJson(Rotation),
            ["state"] = State
        };
    }

    public static WorldItemPersistenceRecord FromJson(JObject value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        return new WorldItemPersistenceRecord
        {
            PersistentItemId = ParseGuid((string)value["persistentItemId"]),
            AuthoredItemKey = (string)value["authoredItemKey"] ?? string.Empty,
            PrefabName = (string)value["prefabName"] ?? string.Empty,
            Revision = (uint?)value["revision"] ?? 0,
            HasPersistentOverride = (bool?)value["hasPersistentOverride"] ?? false,
            IsReplenishableStockFork = (bool?)value["replenishableStockFork"] ?? false,
            Placement = (ItemPlacementKind)((byte?)value["placement"] ?? (byte)ItemPlacementKind.World),
            OwnerIdentity = ParseGuid((string)value["ownerIdentity"]),
            PlacementPlayerIdentity = ParseGuid((string)value["placementPlayerIdentity"]),
            ClaimSlot = (int?)value["claimSlot"] ?? -1,
            ClaimFlags = (ItemInventoryClaimFlags)((byte?)value["claimFlags"] ?? 0),
            WorldParentKind = (ItemWorldParentKind)((byte?)value["worldParentKind"] ?? 0),
            WorldParentNetId = (ushort?)value["worldParentNetId"] ?? 0,
            WorldParentKey = (string)value["worldParentKey"] ?? string.Empty,
            WorldParentPersistentId = (string)value["worldParentPersistentId"] ?? string.Empty,
            AttachedCarPersistentId = (string)value["attachedCarPersistentId"] ?? string.Empty,
            AttachedFront = (bool?)value["attachedFront"] ?? true,
            ParentLocalPosition = VectorFromJson(value["parentLocalPosition"] as JArray),
            ParentLocalRotation = QuaternionFromJson(value["parentLocalRotation"] as JArray),
            Position = VectorFromJson(value["position"] as JArray),
            Rotation = QuaternionFromJson(value["rotation"] as JArray),
            State = value["state"] as JObject
        };
    }

    public static WorldItemPersistenceRecord CreateTombstone(AuthoritativeItemRegistry.Record record) =>
        new()
        {
            AuthoredItemKey = record.AuthoredItemKey ?? string.Empty,
            PrefabName = record.PrefabName ?? string.Empty,
            Revision = record.Revision == uint.MaxValue ? record.Revision : record.Revision + 1,
            Placement = ItemPlacementKind.Destroyed
        };

    private static string FormatGuid(Guid value) => value == Guid.Empty ? string.Empty : value.ToString("D");

    private static Guid ParseGuid(string value) => Guid.TryParse(value, out Guid parsed) ? parsed : Guid.Empty;

    private static JArray VectorToJson(Vector3 value) =>
        JArray.FromObject(new[] { value.x, value.y, value.z });

    private static JArray QuaternionToJson(Quaternion value) =>
        JArray.FromObject(new[] { value.x, value.y, value.z, value.w });

    private static Vector3 VectorFromJson(JArray value) => value?.Count == 3
        ? new Vector3((float)value[0], (float)value[1], (float)value[2])
        : Vector3.zero;

    private static Quaternion QuaternionFromJson(JArray value) => value?.Count == 4
        ? new Quaternion((float)value[0], (float)value[1], (float)value[2], (float)value[3])
        : Quaternion.identity;
}
