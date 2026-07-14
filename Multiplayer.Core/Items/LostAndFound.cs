using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items;

public enum LostItemReason : byte
{
    OwnerDistance,
    InventoryOverflow,
    ContainerCleared,
    GadgetDestinationMissing,
    PurchasedDeliveryFallback,
    SaveRecovery
}

public enum LostItemDecisionKind : byte
{
    Ineligible,
    Protected,
    WaitingForGrace,
    Collect
}

public sealed class LostItemCandidate
{
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    public byte PersistentOwnerPlayerId { get; set; }
    public AuthorityPlacement Placement { get; set; }
    public bool IsGrabbed { get; set; }
    public bool IsSnapped { get; set; }
    public bool IsInMachine { get; set; }
    public bool IsLicensePaper { get; set; }
    public bool IsPersonalItem { get; set; }
    public bool OwnerPositionKnown { get; set; }
    public float OwnerDistanceSquared { get; set; }
    public bool NearestPlayerPositionKnown { get; set; }
    public float NearestPlayerDistanceSquared { get; set; }
    public float EligibleDurationSeconds { get; set; }
}

public readonly struct LostItemDecision
{
    public LostItemDecision(LostItemDecisionKind kind, string reason)
    {
        Kind = kind;
        Reason = reason ?? string.Empty;
    }

    public LostItemDecisionKind Kind { get; }
    public string Reason { get; }
    public bool ShouldCollect => Kind == LostItemDecisionKind.Collect;
}

/// <summary>Pure host collection policy. Unknown state always protects the item.</summary>
public static class LostItemCollectionPolicy
{
    public static LostItemDecision Evaluate(LostItemCandidate item, float ownerDistance,
        float nearbyPlayerProtectionDistance, float graceSeconds)
    {
        if (item == null || item.NetId == 0 || item.PersistentOwnerPlayerId == 0)
            return Ineligible("unowned-or-unbound");
        if (!item.IsPersonalItem)
            return Ineligible("not-a-personal-item");
        if (item.IsLicensePaper)
            return Ineligible("license-paper-excluded");
        if (item.Placement != AuthorityPlacement.World)
            return Ineligible("not-in-world");
        if (item.IsGrabbed || item.IsSnapped || item.IsInMachine)
            return Ineligible("actively-contained-or-interacted");
        if (!item.OwnerPositionKnown)
            return Protected("owner-position-unknown");
        if (item.OwnerDistanceSquared < ownerDistance * ownerDistance)
            return Protected("owner-nearby");
        if (!item.NearestPlayerPositionKnown)
            return Protected("player-positions-unknown");
        if (item.NearestPlayerDistanceSquared < nearbyPlayerProtectionDistance * nearbyPlayerProtectionDistance)
            return Protected("another-player-nearby");
        if (item.EligibleDurationSeconds < graceSeconds)
            return new LostItemDecision(LostItemDecisionKind.WaitingForGrace, "collection-grace-period");
        return new LostItemDecision(LostItemDecisionKind.Collect, "owner-distance-eligible");
    }

    private static LostItemDecision Ineligible(string reason) =>
        new(LostItemDecisionKind.Ineligible, reason);
    private static LostItemDecision Protected(string reason) =>
        new(LostItemDecisionKind.Protected, reason);
}

public sealed class LostItemRecord
{
    public ushort NetId { get; set; }
    public uint Revision { get; set; }
    public byte OwnerPlayerId { get; set; }
    public string OwnerIdentity { get; set; } = string.Empty;
    public string PrefabName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public byte InventoryClaimPlayerId { get; set; }
    public int InventoryClaimSlot { get; set; } = -1;
    public byte InventoryClaimFlags { get; set; }
    public LostItemReason Reason { get; set; }
    public long LostUtcTicks { get; set; }
    public string PersistenceToken { get; set; } = string.Empty;

    public LostItemRecord Clone() => (LostItemRecord)MemberwiseClone();
}

public enum LostItemRegistryResult : byte
{
    Added,
    Updated,
    Removed,
    Invalid,
    DuplicateNetId,
    NotFound,
    WrongOwner,
    StaleRevision
}

/// <summary>One canonical Lost and Found entry per NetId, partitioned by persistent owner.</summary>
public sealed class LostItemRegistry
{
    private readonly Dictionary<ushort, LostItemRecord> records = new();

    public int Count => records.Count;

    public LostItemRegistryResult Add(LostItemRecord record)
    {
        if (record == null || record.NetId == 0 || record.OwnerPlayerId == 0)
            return LostItemRegistryResult.Invalid;
        if (records.ContainsKey(record.NetId))
            return LostItemRegistryResult.DuplicateNetId;
        LostItemRecord stored = record.Clone();
        records.Add(stored.NetId, stored);
        return LostItemRegistryResult.Added;
    }

    public bool TryGet(ushort netId, out LostItemRecord record)
    {
        if (records.TryGetValue(netId, out LostItemRecord stored))
        {
            record = stored.Clone();
            return true;
        }
        record = null;
        return false;
    }

    public IReadOnlyList<LostItemRecord> GetForOwner(byte ownerPlayerId) => records.Values
        .Where(record => record.OwnerPlayerId == ownerPlayerId)
        .OrderBy(record => record.LostUtcTicks)
        .ThenBy(record => record.NetId)
        .Select(record => record.Clone())
        .ToArray();

    public IReadOnlyList<LostItemRecord> GetAll() => records.Values
        .OrderBy(record => record.OwnerIdentity, StringComparer.Ordinal)
        .ThenBy(record => record.LostUtcTicks)
        .ThenBy(record => record.NetId)
        .Select(record => record.Clone())
        .ToArray();

    public LostItemRegistryResult RemoveForRetrieval(ushort netId, byte ownerPlayerId,
        uint expectedRevision, out LostItemRecord removed)
    {
        removed = null;
        if (!records.TryGetValue(netId, out LostItemRecord record))
            return LostItemRegistryResult.NotFound;
        if (record.OwnerPlayerId != ownerPlayerId)
            return LostItemRegistryResult.WrongOwner;
        if (record.Revision != expectedRevision)
            return LostItemRegistryResult.StaleRevision;
        records.Remove(netId);
        removed = record.Clone();
        return LostItemRegistryResult.Removed;
    }

    /// <summary>
    /// Removes an entry whose canonical authority was changed by another host system.
    /// This deliberately does not require the old Lost and Found revision: the transition
    /// which invalidated the entry may already have advanced it.
    /// </summary>
    public LostItemRegistryResult RemoveStale(ushort netId, out LostItemRecord removed)
    {
        removed = null;
        if (netId == 0)
            return LostItemRegistryResult.Invalid;
        if (!records.TryGetValue(netId, out LostItemRecord record))
            return LostItemRegistryResult.NotFound;
        records.Remove(netId);
        removed = record.Clone();
        return LostItemRegistryResult.Removed;
    }

    public void Restore(LostItemRecord record)
    {
        if (record != null && record.NetId != 0 && record.OwnerPlayerId != 0)
            records[record.NetId] = record.Clone();
    }

    public void Clear() => records.Clear();
}

public readonly struct LostItemRetrievalPlan
{
    public LostItemRetrievalPlan(bool accepted, string reason, int targetSlot)
    {
        Accepted = accepted;
        Reason = reason ?? string.Empty;
        TargetSlot = targetSlot;
    }
    public bool Accepted { get; }
    public string Reason { get; }
    public int TargetSlot { get; }
}

public static class LostItemRetrievalPlanner
{
    public static LostItemRetrievalPlan Plan(LostItemRecord record, byte requesterPlayerId,
        uint expectedRevision, int requestedSlot, int existingItemSlot, int inventoryCapacity,
        IReadOnlyCollection<int> occupiedSlots)
    {
        if (record == null || requesterPlayerId == 0)
            return Reject("unknown-lost-item");
        if (record.OwnerPlayerId != requesterPlayerId)
            return Reject("requester-not-persistent-owner");
        if (record.Revision != expectedRevision)
            return Reject("stale-lost-item-revision");
        if (inventoryCapacity <= 0)
            return Reject("inventory-unavailable");

        // Reserved/dropped entries are silhouettes, not genuinely free slots. They cannot be
        // moved by the base game, so restoring the same canonical object must revive that exact
        // slot even though it appears occupied in the inventory summary.
        if (existingItemSlot >= 0 && existingItemSlot < inventoryCapacity)
            return new LostItemRetrievalPlan(true, string.Empty, existingItemSlot);

        HashSet<int> occupied = occupiedSlots == null ? new HashSet<int>() : new HashSet<int>(occupiedSlots);
        if (requestedSlot >= 0 && requestedSlot < inventoryCapacity && !occupied.Contains(requestedSlot))
            return new LostItemRetrievalPlan(true, string.Empty, requestedSlot);
        for (int slot = 0; slot < inventoryCapacity; slot++)
            if (!occupied.Contains(slot))
                return new LostItemRetrievalPlan(true, string.Empty, slot);
        return Reject("inventory-full");
    }

    private static LostItemRetrievalPlan Reject(string reason) => new(false, reason, -1);
}
