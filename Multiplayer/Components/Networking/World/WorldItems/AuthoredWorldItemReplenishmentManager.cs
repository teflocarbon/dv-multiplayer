using DV.CabControls;
using DV.Customization.Gadgets;
using DV.Items;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

/// <summary>
/// Separates a replenishable scene-authored office slot from the persistent item a player takes.
/// The slot is restored only after every player leaves its item-interest neighbourhood.
/// </summary>
internal sealed class AuthoredWorldItemReplenishmentManager
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

    private readonly NetworkedItemManager owner;
    private readonly Dictionary<string, PendingSlot> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> disturbedEmptySince = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, float> abandonedForkEmptySince = new();
    private float nextEvaluationAt;

    internal AuthoredWorldItemReplenishmentManager(NetworkedItemManager owner) => this.owner = owner;

    internal void Clear()
    {
        pending.Clear();
        disturbedEmptySince.Clear();
        abandonedForkEmptySince.Clear();
        nextEvaluationAt = 0f;
    }

    internal static bool IsEligibleAuthoredOfficeProp(NetworkedItem item)
    {
        if (item?.Item?.InventorySpecs == null || !item.IsSceneAuthored ||
            item.Item.InventorySpecs.BelongsToPlayer || item.Item.InventorySpecs.IsEssential || item.Item.IsSnapped ||
            item.GetComponent<ItemContainer>() != null || item.GetComponent<GadgetItem>() != null ||
            item.GetComponent<JobBooklet>() != null || item.GetComponent<JobOverview>() != null ||
            item.GetComponent<JobReport>() != null)
            return false;
        return item.IsReplenishableAuthoredOfficeSlot && !item.IsAuthoredShopSlot;
    }

    internal void OnAuthoritativeTransition(NetworkedItem item, AuthoritativeItemRegistry.Record record,
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
        owner.RemoveAuthoredItemProjection(key, item);
        Guid persistentId = AuthoritativeItemRegistry.PromoteAuthoredToPersistent(item,
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
            if (AnyPlayerInterested(slot.BaselinePosition))
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
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll().ToArray())
        {
            if (record == null || !record.IsReplenishableStockFork ||
                record.PersistentOwnerPlayerId != 0 || record.PersistentOwnerIdentity != Guid.Empty ||
                record.InventoryClaimPlayerId != 0 || record.InventoryClaimSlot >= 0 ||
                NetworkedLostAndFoundManager.Contains(record.NetId) ||
                record.Placement is not (ItemPlacementKind.World or ItemPlacementKind.StaticParent or
                    ItemPlacementKind.TrainInterior) ||
                !NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
                continue;

            observed.Add(record.NetId);
            Vector3 currentPosition = item.transform.position - WorldMover.currentMove;
            if (AnyPlayerInterested(currentPosition))
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
            DebugRuntime.Publish("item-world", "world-item.abandoned-stock-fork-removed",
                DebugRuntimeSide.Server, entityType: "Item", entityId: record.NetId.ToString(), data: new()
                {
                    ["persistentItemId"] = record.PersistentItemId == Guid.Empty
                        ? string.Empty : record.PersistentItemId.ToString("D"),
                    ["prefabName"] = record.PrefabName,
                    ["positionAbsolute"] = DebugValueSnapshotter.Snapshot(currentPosition),
                    ["emptyAreaSeconds"] = AbandonedForkCleanupSeconds
                });
            UnityEngine.Object.Destroy(item.gameObject);
        }

        foreach (ushort netId in abandonedForkEmptySince.Keys.Where(id => !observed.Contains(id)).ToArray())
            abandonedForkEmptySince.Remove(netId);
    }

    private void EvaluateDisturbedAuthoredSlots(float now)
    {
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (AuthoritativeItemRegistry.Record record in AuthoritativeItemRegistry.GetAll().ToArray())
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
            if (!displaced || AnyPlayerInterested(record.BaselinePosition))
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
                !owner.ResetAuthoredItemToBaseline(item, record))
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

    private static bool AnyPlayerInterested(Vector3 absolutePosition)
    {
        WorldItemCellCoord slotCell = WorldItemCellCoord.FromAbsolute(absolutePosition);
        return NetworkLifecycle.Instance.Server.ServerPlayers.Any(player => player != null &&
            player.LoadingState >= PlayerLoadingState.ReadyForItems &&
            WorldItemCellCoord.Neighbourhood(WorldItemCellCoord.FromAbsolute(player.AbsoluteWorldPosition))
                .Contains(slotCell));
    }

    private bool TryCreateReplacement(PendingSlot slot)
    {
        if (!owner.TryGetItemPrefab(slot.PrefabName, out GameObject prefab))
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
        AuthoritativeItemRegistry.Record record = AuthoritativeItemRegistry.Ensure(replacement);
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
