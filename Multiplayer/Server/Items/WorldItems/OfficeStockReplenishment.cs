using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Items;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Projection;
using Multiplayer.Core.Items.WorldItems;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Items.WorldItems;

/// <summary>
/// Separates a replenishable scene-authored office slot from the persistent item a player takes.
/// The slot is restored only after every player leaves its item-interest neighbourhood.
/// </summary>
internal sealed class OfficeStockReplenishment
{
    private const float EmptyAreaDwellSeconds = 3f;
    private const float DisturbedAreaCleanupSeconds = 15f;
    private const float AbandonedForkCleanupSeconds = 30f;
    private const float EvaluationIntervalSeconds = 0.5f;
    private const float PositionResetTolerance = 0.25f;
    private const float RotationResetToleranceDegrees = 5f;

    private sealed class PendingSlot
    {
        public string Key;
        public string PrefabName;
        public Vector3 BaselinePosition;
        public Quaternion BaselineRotation;
        public ushort TakenNetId;
        public bool AuthoredShopSlot;
        public float EmptySince = -1f;
    }

    private readonly ServerItemManager owner;
    private readonly Dictionary<string, PendingSlot> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> disturbedEmptySince = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, float> abandonedForkEmptySince = new();
    private float nextEvaluationAt;

    internal OfficeStockReplenishment(ServerItemManager owner) => this.owner = owner;

    internal void Clear()
    {
        pending.Clear();
        disturbedEmptySince.Clear();
        abandonedForkEmptySince.Clear();
        nextEvaluationAt = 0f;
    }

    private static bool IsEligibleAuthoredOfficeProp(NetworkedItem item) =>
        AuthoredItems.IsEligibleOfficeProp(item);

    internal void OnAuthoritativeTransition(NetworkedItem item, LiveItems.LiveItem record,
        ItemPlacementKind previousPlacement, bool appliesPlacement)
    {
        if (!NetworkLifecycle.Instance.IsHost() || !appliesPlacement || item == null || record == null ||
            record.Placement is not (ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory) ||
            previousPlacement is not (ItemPlacementKind.World or ItemPlacementKind.StaticParent) ||
            !IsEligibleAuthoredOfficeProp(item))
            return;

        string key = item.AuthoredItemKey;
        PendingSlot slot = new()
        {
            Key = key,
            PrefabName = record.PrefabName,
            BaselinePosition = record.BaselinePosition,
            BaselineRotation = record.BaselineRotation,
            TakenNetId = item.NetId,
            AuthoredShopSlot = item.IsAuthoredShopSlot
        };
        owner.Shell.Authored.Remove(key, item);
        Guid persistentId = LiveItems.PromoteAuthoredToPersistent(item,
            replenishableStockFork: true);
        item.DetachAuthoredItemKey();
        pending[key] = slot;
        Publish("world-item.authored-slot-detached", slot, new()
        {
            ["persistentItemId"] = persistentId == Guid.Empty ? string.Empty : persistentId.ToString("D")
        });
    }

    internal void Update()
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        float now = Time.realtimeSinceStartup;
        if (now < nextEvaluationAt)
            return;
        nextEvaluationAt = now + EvaluationIntervalSeconds;

        foreach (PendingSlot slot in pending.Values.ToArray())
        {
            if (AnyPlayerInterested(slot.BaselinePosition, lying: false))
            {
                slot.EmptySince = -1f;
                continue;
            }
            if (slot.EmptySince < 0f)
            {
                slot.EmptySince = now;
                continue;
            }
            if (now - slot.EmptySince < EmptyAreaDwellSeconds)
                continue;
            if (TryCreateReplacement(slot))
                pending.Remove(slot.Key);
        }

        EvaluateDisturbedAuthoredSlots(now);
        EvaluateAbandonedForks(now);
    }

    private void EvaluateAbandonedForks(float now)
    {
        HashSet<ushort> observed = new();
        foreach (LiveItems.LiveItem record in LiveItems.GetAll().ToArray())
        {
            if (record == null || !record.IsReplenishableStockFork ||
                !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null ||
                !OfficeStockRules.MayClearFork(record.Owner != Guid.Empty, record.InventoryClaimSlot >= 0,
                    LostAndFoundServer.Contains(record.NetId), (LivePlacement)(byte)record.Placement,
                    Moving(item), item.Item != null && item.Item.IsGrabbed()))
            {
                if (record != null)
                    abandonedForkEmptySince.Remove(record.NetId);
                continue;
            }

            observed.Add(record.NetId);
            Vector3 currentPosition = item.transform.position - WorldMover.currentMove;
            if (AnyPlayerInterested(currentPosition, lying: true) || AnyPlayerHas(item))
            {
                abandonedForkEmptySince.Remove(record.NetId);
                continue;
            }
            if (!abandonedForkEmptySince.TryGetValue(record.NetId, out float emptySince))
            {
                abandonedForkEmptySince[record.NetId] = now;
                continue;
            }
            if (now - emptySince < AbandonedForkCleanupSeconds)
                continue;

            abandonedForkEmptySince.Remove(record.NetId);
            if (InstalledItems.RefuseDestroy(item, "an abandoned stock fork"))
                continue;
            DebugRuntime.Publish("item-world", "world-item.abandoned-stock-fork-removed",
                DebugRuntimeSide.Server, entityType: "Item", entityId: record.NetId.ToString(), data: new()
                {
                    ["persistentItemId"] = record.PersistentItemId == Guid.Empty
                        ? string.Empty : record.PersistentItemId.ToString("D"),
                    ["prefabName"] = record.PrefabName,
                    ["positionAbsolute"] = DebugValueSnapshotter.Snapshot(currentPosition),
                    ["emptyAreaSeconds"] = AbandonedForkCleanupSeconds
                });
            item.NoteRemovalCause(ItemChangeLog.Removal(global::Multiplayer.Core.Items.ChangeCauses.ItemRemovalReason.AbandonedStockForkCleared,
                detail: $"unowned office stock, nobody near for {AbandonedForkCleanupSeconds:F0} s"));
            UnityEngine.Object.Destroy(item.gameObject);
        }

        foreach (ushort netId in abandonedForkEmptySince.Keys.Where(id => !observed.Contains(id)).ToArray())
            abandonedForkEmptySince.Remove(netId);
    }

    private void EvaluateDisturbedAuthoredSlots(float now)
    {
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (LiveItems.LiveItem record in LiveItems.GetAll().ToArray())
        {
            string key = record?.AuthoredItemKey ?? string.Empty;
            if (!WorldItemStableIdentity.IsValid(key) || pending.ContainsKey(key) ||
                record.Placement is not (ItemPlacementKind.World or ItemPlacementKind.StaticParent) ||
                !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null ||
                !IsEligibleAuthoredOfficeProp(item))
                continue;

            observed.Add(key);
            Vector3 observedPosition = item.transform.position - WorldMover.currentMove;
            bool displaced = (record.Position - record.BaselinePosition).sqrMagnitude >
                             PositionResetTolerance * PositionResetTolerance ||
                             (observedPosition - record.BaselinePosition).sqrMagnitude >
                             PositionResetTolerance * PositionResetTolerance ||
                             Quaternion.Angle(record.Rotation, record.BaselineRotation) >
                             RotationResetToleranceDegrees ||
                             Quaternion.Angle(item.transform.rotation, record.BaselineRotation) >
                             RotationResetToleranceDegrees;
            if (!displaced || !OfficeStockRules.MayReset(AnyPlayerInterested(record.BaselinePosition, lying: false),
                    AnyPlayerInterested(observedPosition, lying: true) || AnyPlayerHas(item), Moving(item),
                    item.Item != null && item.Item.IsGrabbed()))
            {
                disturbedEmptySince.Remove(key);
                continue;
            }

            if (!disturbedEmptySince.TryGetValue(key, out float emptySince))
            {
                disturbedEmptySince[key] = now;
                continue;
            }
            if (now - emptySince < DisturbedAreaCleanupSeconds ||
                !owner.ResetAuthoredItemToBaseline(item, record,
                    $"disturbed, and nobody near it for {DisturbedAreaCleanupSeconds:0} s"))
                continue;

            disturbedEmptySince.Remove(key);
            DebugRuntime.Publish("item-world", "world-item.authored-slot-reset",
                DebugRuntimeSide.Server, entityType: "WorldItemSlot", entityId: key, data: new()
                {
                    ["itemNetId"] = item.NetId,
                    ["prefabName"] = record.PrefabName,
                    ["baselinePosition"] = DebugValueSnapshotter.Snapshot(record.BaselinePosition),
                    ["emptyAreaSeconds"] = DisturbedAreaCleanupSeconds
                });
        }

        foreach (string key in disturbedEmptySince.Keys.Where(key => !observed.Contains(key)).ToArray())
            disturbedEmptySince.Remove(key);
    }

    // A player still loading protects every place (OfficeStockRules.Protects, OI-SYNC-10). Where an
    // item already lies (lying), a player protects it as far as interest keeps items, 5x5 cells:
    // before the R7 fixes one a cell and a half away still had the prop when it was reset or cleared
    // in front of them. An empty slot or a prop's place is where a new item would appear, and new
    // items enter interest at 3x3: at 5x5 a player shunting a cell or two away kept an office empty.
    private static bool AnyPlayerInterested(Vector3 absolutePosition, bool lying)
    {
        WorldItemCellCoord slotCell = WorldItemCellCoord.FromAbsolute(absolutePosition);
        int radius = lying ? InterestCells.KeepRadius : InterestCells.EnterRadius;
        return NetworkServer.Instance.ServerPlayers.Any(player => player != null &&
            OfficeStockRules.Protects(player.LoadingState >= PlayerLoadingState.ReadyForItems,
                WorldItemCellCoord.Neighbourhood(WorldItemCellCoord.FromAbsolute(player.AbsoluteWorldPosition),
                    radius).Contains(slotCell)));
    }

    // A player whose interest has the item, wherever it lies (pinned there too). Not KnownItems:
    // an item materialized from a container is known to every player, near or not, and stays known.
    // A simulated one is Moving.
    private static bool AnyPlayerHas(NetworkedItem item) =>
        NetworkServer.Instance.ServerPlayers.Any(player => player != null && player.NearbyItems.ContainsKey(item));

    // Flying or sliding under a lease: its simulator has it, wherever it is going.
    private bool Moving(NetworkedItem item) => owner.Shell.HasActiveSpatialState(item.NetId);

    private bool TryCreateReplacement(PendingSlot slot)
    {
        if (!owner.Shell.TryGetItemPrefab(slot.PrefabName, out GameObject prefab))
        {
            Publish("world-item.authored-slot-replenishment-failed", slot,
                new() { ["reason"] = "prefab-not-found" }, DebugSeverity.Error);
            return false;
        }
        GameObject instance = UnityEngine.Object.Instantiate(prefab,
            slot.BaselinePosition + WorldMover.currentMove, slot.BaselineRotation);
        instance.name = slot.PrefabName;
        NetworkedItem replacement = instance.GetComponent<NetworkedItem>() ?? instance.AddComponent<NetworkedItem>();
        replacement.AdoptAuthoredItemKey(slot.Key, replenishableOfficeSlot: true,
            authoredShopSlot: slot.AuthoredShopSlot);
        LiveItems.LiveItem record = LiveItems.Ensure(replacement);
        if (record == null)
        {
            UnityEngine.Object.Destroy(instance);
            Publish("world-item.authored-slot-replenishment-failed", slot,
                new() { ["reason"] = "authority-registration-failed" }, DebugSeverity.Error);
            return false;
        }
        record.BaselinePosition = slot.BaselinePosition;
        record.BaselineRotation = slot.BaselineRotation;
        owner.RegisterHostWorldItem(replacement);
        Publish("world-item.authored-slot-replenished", slot,
            new() { ["replacementNetId"] = replacement.NetId });
        return true;
    }

    private static void Publish(string eventType, PendingSlot slot,
        Dictionary<string, object> extra = null, DebugSeverity severity = DebugSeverity.Info)
    {
        Dictionary<string, object> data = extra ?? new Dictionary<string, object>();
        data["authoredItemKey"] = slot.Key;
        data["prefabName"] = slot.PrefabName;
        data["takenNetId"] = slot.TakenNetId;
        data["baselinePosition"] = DebugValueSnapshotter.Snapshot(slot.BaselinePosition);
        DebugRuntime.Publish("item-world", eventType, DebugRuntimeSide.Server, severity,
            entityType: "WorldItemSlot", entityId: slot.Key, data: data);
    }
}
