using System;

namespace Multiplayer.Core.Items;

[Flags]
public enum StorageMembership : byte
{
    None = 0,
    Inventory = 1,
    World = 2,
    LostAndFound = 4,
    ItemContainer = 8
}

public interface IStorageView
{
    bool IsAvailable { get; }
    StorageMembership Membership { get; }
}

public readonly struct StorageTransitionPlan
{
    public StorageTransitionPlan(bool accepted, StorageMembership remove,
        StorageMembership add, bool repairedMultipleMembership, string reason)
    {
        Accepted = accepted;
        Remove = remove;
        Add = add;
        RepairedMultipleMembership = repairedMultipleMembership;
        Reason = reason ?? string.Empty;
    }

    public bool Accepted { get; }
    public StorageMembership Remove { get; }
    public StorageMembership Add { get; }
    public bool RepairedMultipleMembership { get; }
    public string Reason { get; }
}

public static class StorageTransitionPlanner
{
    public static StorageTransitionPlan Plan(IStorageView view, StorageMembership target)
    {
        if (view == null || !view.IsAvailable)
            return new StorageTransitionPlan(false, StorageMembership.None,
                StorageMembership.None, false, "storage-unavailable");
        if (target != StorageMembership.None && !IsSingleBit(target))
            return new StorageTransitionPlan(false, StorageMembership.None,
                StorageMembership.None, false, "invalid-storage-target");

        StorageMembership current = view.Membership;
        StorageMembership remove = current & ~target;
        StorageMembership add = target != StorageMembership.None && !current.HasFlag(target)
            ? target
            : StorageMembership.None;
        bool multiple = CountBits((byte)current) > 1;
        return new StorageTransitionPlan(true, remove, add, multiple, string.Empty);
    }

    private static bool IsSingleBit(StorageMembership value)
    {
        byte raw = (byte)value;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }

    private static int CountBits(byte value)
    {
        int count = 0;
        while (value != 0)
        {
            value &= (byte)(value - 1);
            count++;
        }
        return count;
    }
}
