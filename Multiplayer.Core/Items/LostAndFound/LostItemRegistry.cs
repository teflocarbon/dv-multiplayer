using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.LostAndFound;

public enum LostItemRegistryResult : byte
{
    Added,
    Updated,
    Removed,
    Invalid,
    DuplicateNetId,
    DuplicatePersistentItemId,
    DuplicateHandle,
    NotFound,
    WrongOwner,
    StaleRevision
}

/// <summary>
/// Host Lost and Found records keyed internally by their current runtime NetId, with a durable
/// UUID for save identity and a compact process-local handle for network requests.
/// </summary>
public sealed class LostItemRegistry
{
    private readonly Dictionary<ushort, LostItemRecord> records = new();
    private readonly Dictionary<uint, ushort> handleToNetId = new();
    private readonly Dictionary<Guid, ushort> persistentIdToNetId = new();
    private uint nextHandle = 1;

    public int Count => records.Count;

    public LostItemRegistryResult Add(LostItemRecord record)
    {
        if (record == null || record.PersistentItemId == Guid.Empty || record.NetId == 0 ||
            string.IsNullOrEmpty(record.OwnerIdentity))
            return LostItemRegistryResult.Invalid;
        if (records.ContainsKey(record.NetId))
            return LostItemRegistryResult.DuplicateNetId;
        if (persistentIdToNetId.ContainsKey(record.PersistentItemId))
            return LostItemRegistryResult.DuplicatePersistentItemId;
        LostItemRecord stored = record.Clone();
        if (stored.Handle == 0)
            stored.Handle = AllocateHandle();
        else if (handleToNetId.ContainsKey(stored.Handle))
            return LostItemRegistryResult.DuplicateHandle;
        records.Add(stored.NetId, stored);
        handleToNetId.Add(stored.Handle, stored.NetId);
        persistentIdToNetId.Add(stored.PersistentItemId, stored.NetId);
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

    public bool TryGetByHandle(uint handle, out LostItemRecord record)
    {
        if (handle != 0 && handleToNetId.TryGetValue(handle, out ushort netId))
            return TryGet(netId, out record);
        record = null;
        return false;
    }

    public bool TryGetByPersistentId(Guid persistentItemId, out LostItemRecord record)
    {
        if (persistentItemId != Guid.Empty &&
            persistentIdToNetId.TryGetValue(persistentItemId, out ushort netId))
            return TryGet(netId, out record);
        record = null;
        return false;
    }

    /// <summary>An owner's entries, by their identity, whether or not they're connected.</summary>
    public IReadOnlyList<LostItemRecord> GetForOwner(string ownerIdentity) => records.Values
        .Where(record => SameIdentity(record.OwnerIdentity, ownerIdentity))
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

    public LostItemRegistryResult RemoveForRetrieval(ushort netId, string ownerIdentity,
        uint expectedRevision, out LostItemRecord removed)
    {
        removed = null;
        if (!records.TryGetValue(netId, out LostItemRecord record))
            return LostItemRegistryResult.NotFound;
        if (!SameIdentity(record.OwnerIdentity, ownerIdentity))
            return LostItemRegistryResult.WrongOwner;
        if (record.Revision != expectedRevision)
            return LostItemRegistryResult.StaleRevision;
        records.Remove(netId);
        handleToNetId.Remove(record.Handle);
        persistentIdToNetId.Remove(record.PersistentItemId);
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
        handleToNetId.Remove(record.Handle);
        persistentIdToNetId.Remove(record.PersistentItemId);
        removed = record.Clone();
        return LostItemRegistryResult.Removed;
    }

    /// <summary>Whether two owner identities name the same player (saved ones may differ in case).</summary>
    public static bool SameIdentity(string a, string b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether any record belongs to this owner identity.</summary>
    public bool HasOwner(string ownerIdentity) =>
        records.Values.Any(record => SameIdentity(record.OwnerIdentity, ownerIdentity));

    /// <summary>
    /// The owner's identity changed (an install GUID re-keyed to a Steam account): their records
    /// move to the new one. Returns how many changed.
    /// </summary>
    public int RekeyOwner(string fromIdentity, string toIdentity)
    {
        if (string.IsNullOrEmpty(fromIdentity) || string.IsNullOrEmpty(toIdentity))
            return 0;
        int changed = 0;
        foreach (LostItemRecord record in records.Values)
        {
            if (!string.Equals(record.OwnerIdentity, fromIdentity, StringComparison.OrdinalIgnoreCase))
                continue;
            record.OwnerIdentity = toIdentity;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// The item took a new id (a copy re-minted by the one-home repair, D7): its record follows, and
    /// the old id is left to the item that keeps it. False, with nothing changed, when the item has
    /// no record here or the new id is another item's.
    /// </summary>
    public bool RekeyItem(ushort netId, Guid fresh)
    {
        if (fresh == Guid.Empty || !records.TryGetValue(netId, out LostItemRecord record))
            return false;
        if (persistentIdToNetId.TryGetValue(fresh, out ushort other) && other != netId)
            return false;
        if (persistentIdToNetId.TryGetValue(record.PersistentItemId, out ushort mapped) && mapped == netId)
            persistentIdToNetId.Remove(record.PersistentItemId);
        record.PersistentItemId = fresh;
        persistentIdToNetId[fresh] = netId;
        return true;
    }

    /// <summary>The host moved the item's retrieval claim (ClaimSlotPolicy): keep the saved copy in step.</summary>
    public bool SetInventoryClaimSlot(ushort netId, int slot)
    {
        if (!records.TryGetValue(netId, out LostItemRecord record))
            return false;
        record.InventoryClaimSlot = slot;
        return true;
    }

    public LostItemRegistryResult Restore(LostItemRecord record)
    {
        if (record == null || record.PersistentItemId == Guid.Empty || record.NetId == 0 ||
            string.IsNullOrEmpty(record.OwnerIdentity))
            return LostItemRegistryResult.Invalid;
        if (persistentIdToNetId.TryGetValue(record.PersistentItemId, out ushort persistentMapped) &&
            persistentMapped != record.NetId)
            return LostItemRegistryResult.DuplicatePersistentItemId;
        if (records.TryGetValue(record.NetId, out LostItemRecord previous))
        {
            handleToNetId.Remove(previous.Handle);
            persistentIdToNetId.Remove(previous.PersistentItemId);
        }
        LostItemRecord stored = record.Clone();
        if (stored.Handle == 0 || (handleToNetId.TryGetValue(stored.Handle, out ushort mapped) &&
            mapped != stored.NetId))
            stored.Handle = AllocateHandle();
        records[stored.NetId] = stored;
        handleToNetId[stored.Handle] = stored.NetId;
        persistentIdToNetId[stored.PersistentItemId] = stored.NetId;
        return LostItemRegistryResult.Updated;
    }

    public void Clear()
    {
        records.Clear();
        handleToNetId.Clear();
        persistentIdToNetId.Clear();
        nextHandle = 1;
    }

    private uint AllocateHandle()
    {
        while (nextHandle == 0 || handleToNetId.ContainsKey(nextHandle))
            nextHandle++;
        return nextHandle++;
    }
}
