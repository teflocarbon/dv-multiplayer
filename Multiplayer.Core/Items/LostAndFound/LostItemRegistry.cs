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
            record.OwnerPlayerId == 0)
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

    /// <summary>
    /// Owners are matched by session player id, which a rejoining player may not get back: point
    /// the owner's records at their new id. Returns how many changed.
    /// </summary>
    public int RebindOwner(string ownerIdentity, byte ownerPlayerId)
    {
        if (string.IsNullOrEmpty(ownerIdentity) || ownerPlayerId == 0)
            return 0;
        int changed = 0;
        foreach (LostItemRecord record in records.Values)
        {
            if (!string.Equals(record.OwnerIdentity, ownerIdentity, StringComparison.OrdinalIgnoreCase) ||
                record.OwnerPlayerId == ownerPlayerId)
                continue;
            record.OwnerPlayerId = ownerPlayerId;
            changed++;
        }
        return changed;
    }

    /// <summary>Whether any record belongs to this owner identity.</summary>
    public bool HasOwner(string ownerIdentity) =>
        !string.IsNullOrEmpty(ownerIdentity) && records.Values.Any(record =>
            string.Equals(record.OwnerIdentity, ownerIdentity, StringComparison.OrdinalIgnoreCase));

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
    /// The owner left: release their session id so a player who is given it next does not see or
    /// retrieve these items. Records with an identity wait for <see cref="RebindOwner"/>.
    /// </summary>
    public int UnbindOwner(byte ownerPlayerId)
    {
        if (ownerPlayerId == 0)
            return 0;
        int changed = 0;
        foreach (LostItemRecord record in records.Values)
        {
            if (record.OwnerPlayerId != ownerPlayerId || string.IsNullOrEmpty(record.OwnerIdentity))
                continue;
            record.OwnerPlayerId = ItemTransition.AbsentOwnerId;
            changed++;
        }
        return changed;
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
            record.OwnerPlayerId == 0)
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
