using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Packets.Common;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Debugging;

public static class DebugPacketProjectorSelfTests
{
    public static void Run()
    {
        Dictionary<string, object> thrown = Project(ItemUpdateData.ItemUpdateType.ItemState, ItemState.Thrown);
        Require(thrown, "position", "rotation", "throwDirection"); Omit(thrown, "playerId", "carNetId", "attachedFront", "prefabName", "states");

        Dictionary<string, object> hand = Project(ItemUpdateData.ItemUpdateType.ItemState, ItemState.InHand);
        Require(hand, "playerId"); Omit(hand, "position", "rotation", "throwDirection", "carNetId", "attachedFront", "prefabName", "states");

        Dictionary<string, object> inventory = Project(ItemUpdateData.ItemUpdateType.ItemState, ItemState.InInventory);
        Require(inventory, "playerId"); Omit(inventory, "position", "throwDirection", "carNetId");

        Dictionary<string, object> attached = Project(ItemUpdateData.ItemUpdateType.ItemState, ItemState.Attached);
        Require(attached, "carNetId", "attachedFront"); Omit(attached, "position", "rotation", "throwDirection", "playerId");

        Dictionary<string, object> create = Project(ItemUpdateData.ItemUpdateType.Create, ItemState.Dropped);
        Require(create, "prefabName", "position", "rotation", "states");

        Dictionary<string, object> destroy = Project(ItemUpdateData.ItemUpdateType.Destroy, ItemState.Thrown);
        Require(destroy, "updateType", "itemNetId"); Omit(destroy, "itemState", "prefabName", "position", "states", "playerId");

        Dictionary<string, object> objectState = Project(ItemUpdateData.ItemUpdateType.ObjectState, ItemState.Thrown);
        Require(objectState, "itemState", "states"); Omit(objectState, "position", "rotation", "throwDirection", "prefabName");

        Dictionary<string, object> full = Project(ItemUpdateData.ItemUpdateType.FullSync, ItemState.Thrown);
        Require(full, "position", "rotation", "throwDirection", "states"); Omit(full, "playerId", "carNetId", "attachedFront", "prefabName");

        DebugPacketProjection packet = DebugPacketProjectorRegistry.Project(new CommonItemUpdatePacket { ItemData = Item(ItemUpdateData.ItemUpdateType.ItemState, ItemState.Thrown) }, 1);
        string json = DebugJson.Serialize(packet.Wire);
        foreach (string forbidden in new[] { "normalized", "sqrMagnitude", "maximum depth", "eulerAngles" })
            if (json.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("Packet projection contains recursive Unity property: " + forbidden);

        string fallback = DebugJson.Serialize(DebugStrictSnapshotter.Snapshot(new FallbackFixture { Position = new Vector3(1, 2, 3), IgnoredProperty = 42 }));
        if (fallback.Contains("IgnoredProperty") || fallback.Contains("normalized") || !fallback.Contains("\"x\":1.0"))
            throw new InvalidOperationException("Strict fallback projected a property or failed to project a vector directly: " + fallback);
    }

    private static Dictionary<string, object> Project(ItemUpdateData.ItemUpdateType updateType, ItemState state) => DebugPacketProjectorRegistry.ProjectItemUpdate(Item(updateType, state));
    private static ItemUpdateData Item(ItemUpdateData.ItemUpdateType updateType, ItemState state) => new()
    {
        UpdateType = updateType, ItemNetId = 78, PrefabName = "Cup2", ItemState = state,
        ItemPosition = new Vector3(9469.9f, 120.599f, 13616.892f), ItemRotation = new Quaternion(.175f, .732f, -.211f, .624f),
        ThrowDirection = new Vector3(.839f, -.528f, -.132f), PlayerId = 1, CarNetId = 22, AttachedFront = true,
        States = new Dictionary<string, object> { ["enabled"] = true, ["notch"] = 2 }
    };
    private static void Require(Dictionary<string, object> value, params string[] keys) { foreach (string key in keys) if (!value.ContainsKey(key)) throw new InvalidOperationException($"Projection should contain '{key}'."); }
    private static void Omit(Dictionary<string, object> value, params string[] keys) { foreach (string key in keys) if (value.ContainsKey(key)) throw new InvalidOperationException($"Projection should omit '{key}'."); }

    private sealed class FallbackFixture
    {
        public Vector3 Position;
        public int IgnoredProperty { get; set; }
    }
}
