using Multiplayer.Shared.Protocol.Data.Items;
using Newtonsoft.Json.Linq;
using System;
using System.Text;
using UnityEngine;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// One item as data: what <c>Multiplayer.Items</c> saves of it (docs/design/items/save-load.md,
/// "The key"), and what a cold inventory keeps. Owned and unowned rows differ in what they write: an
/// owned item's owner, holder, slot and claim are its ledger entry's, so its row has only its pose
/// and its state; an unowned item's row is the only place its home is.
/// </summary>
internal sealed class SavedItemRow
{
    public Guid PersistentItemId { get; set; }
    public string AuthoredItemKey { get; set; } = string.Empty;
    public string PrefabName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public uint Revision { get; set; }
    public bool HasPersistentOverride { get; set; }
    public bool IsReplenishableStockFork { get; set; }
    public ItemPlacementKind Placement { get; set; } = ItemPlacementKind.World;
    /// <summary>In memory only: an owned row's owner is its ledger entry's.</summary>
    public Guid OwnerIdentity { get; set; }
    public Guid PlacementPlayerIdentity { get; set; }
    /// <summary>In memory only: an essential's claim is its ledger entry's.</summary>
    public int ClaimSlot { get; set; } = -1;
    /// <summary>Its slot of the holder's inventory (OI-SLOT-01), or -1.</summary>
    public int Slot { get; set; } = -1;
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
    /// <summary>A stored item's container (an unowned one's; an owned one's is its home).</summary>
    public Guid ContainerId { get; set; }
    public uint StateVersion { get; set; }
    /// <summary>The item's own <c>ItemSaveData</c> JSON, or null for an item without (a mug).</summary>
    public JObject State { get; set; }
    /// <summary>A stored item's state that isn't JSON, kept as it was.</summary>
    public byte[] StateBytes { get; set; }

    public bool IsTombstone => Placement == ItemPlacementKind.Destroyed;

    /// <summary>The row as <c>Multiplayer.Items</c> writes it: an owned row without its home, an unowned row with it.</summary>
    public JObject ToJson(bool owned)
    {
        JObject row = new()
        {
            ["id"] = FormatGuid(PersistentItemId),
            ["prefab"] = PrefabName ?? string.Empty
        };
        if (!string.IsNullOrEmpty(AuthoredItemKey))
            row["authoredKey"] = AuthoredItemKey;
        if (!string.IsNullOrEmpty(DisplayName))
            row["display"] = DisplayName;
        if (!owned)
        {
            row["place"] = Placement.ToString();
            if (PlacementPlayerIdentity != Guid.Empty)
                row["holder"] = FormatGuid(PlacementPlayerIdentity);
            if (Slot >= 0)
                row["slot"] = Slot;
            if (ContainerId != Guid.Empty)
                row["container"] = FormatGuid(ContainerId);
            if (Placement == ItemPlacementKind.Attached)
            {
                row["attachedCar"] = AttachedCarPersistentId ?? string.Empty;
                row["attachedFront"] = AttachedFront;
            }
            if (Placement == ItemPlacementKind.SnappedAttachment)
                row["snappedTo"] = WorldParentKey ?? string.Empty;
        }
        if (Revision != 0)
            row["revision"] = Revision;
        if (HasPersistentOverride)
            row["override"] = true;
        if (IsReplenishableStockFork)
            row["stockFork"] = true;
        if (Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or ItemPlacementKind.StaticParent or
            ItemPlacementKind.SnappedAttachment or ItemPlacementKind.Attached)
        {
            row["position"] = VectorToJson(Position);
            row["rotation"] = QuaternionToJson(Rotation);
        }
        if (WorldParentKind != ItemWorldParentKind.World)
            row["parent"] = new JObject
            {
                ["kind"] = WorldParentKind.ToString(),
                ["key"] = WorldParentKey ?? string.Empty,
                ["car"] = WorldParentPersistentId ?? string.Empty,
                ["position"] = VectorToJson(ParentLocalPosition),
                ["rotation"] = QuaternionToJson(ParentLocalRotation)
            };
        if (StateVersion != 0)
            row["stateVersion"] = StateVersion;
        if (State != null)
            row["state"] = State;
        else if (StateBytes?.Length > 0)
            row["stateBytes"] = Convert.ToBase64String(StateBytes);
        return row;
    }

    /// <summary>
    /// A saved row. Throws on one it can't read, which the caller keeps as it was: one with no prefab,
    /// or with neither an id nor a scene key (a scene item's override has only its key).
    /// </summary>
    public static SavedItemRow FromJson(JObject value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        Guid id = ParseGuid((string)value["id"]);
        if (id == Guid.Empty && string.IsNullOrEmpty((string)value["authoredKey"]))
            throw new FormatException("a row with neither an id nor a scene key");
        string prefab = (string)value["prefab"];
        if (string.IsNullOrEmpty(prefab))
            throw new FormatException("a row without a prefab");
        JObject parent = value["parent"] as JObject;
        SavedItemRow row = new()
        {
            PersistentItemId = id,
            AuthoredItemKey = (string)value["authoredKey"] ?? string.Empty,
            PrefabName = prefab,
            DisplayName = (string)value["display"] ?? string.Empty,
            Revision = (uint?)value["revision"] ?? 0,
            HasPersistentOverride = (bool?)value["override"] ?? false,
            IsReplenishableStockFork = (bool?)value["stockFork"] ?? false,
            Placement = Enum.TryParse((string)value["place"], out ItemPlacementKind place) ? place : ItemPlacementKind.World,
            PlacementPlayerIdentity = ParseGuid((string)value["holder"]),
            Slot = (int?)value["slot"] ?? -1,
            ContainerId = ParseGuid((string)value["container"]),
            AttachedCarPersistentId = (string)value["attachedCar"] ?? string.Empty,
            AttachedFront = (bool?)value["attachedFront"] ?? true,
            WorldParentKind = parent != null && Enum.TryParse((string)parent["kind"], out ItemWorldParentKind kind)
                ? kind : ItemWorldParentKind.World,
            WorldParentKey = (string)parent?["key"] ?? (string)value["snappedTo"] ?? string.Empty,
            WorldParentPersistentId = (string)parent?["car"] ?? string.Empty,
            ParentLocalPosition = VectorFromJson(parent?["position"] as JArray),
            ParentLocalRotation = QuaternionFromJson(parent?["rotation"] as JArray),
            Position = VectorFromJson(value["position"] as JArray),
            Rotation = QuaternionFromJson(value["rotation"] as JArray),
            StateVersion = (uint?)value["stateVersion"] ?? 0,
            State = value["state"] as JObject
        };
        if (row.State == null && value["stateBytes"] is JValue bytes)
            row.StateBytes = Convert.FromBase64String((string)bytes);
        return row;
    }

    /// <summary>A stored item's state as the container graph keeps it: the JSON as UTF-8 bytes.</summary>
    public byte[] DetachedState() =>
        State != null ? Encoding.UTF8.GetBytes(State.ToString(Newtonsoft.Json.Formatting.None)) : StateBytes ?? Array.Empty<byte>();

    /// <summary>Takes a stored item's state from the container graph's bytes: JSON when it parses, else the bytes.</summary>
    public void TakeDetachedState(byte[] bytes)
    {
        State = null;
        StateBytes = null;
        if (bytes == null || bytes.Length == 0)
            return;
        try
        {
            State = JObject.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception)
        {
            StateBytes = bytes;
        }
    }

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
