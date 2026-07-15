using DV.CabControls;
using Multiplayer.Core.Items;
using UnityEngine;

namespace Multiplayer.Integrations.Storage;

internal interface IStorageRuntimeAdapter
{
    StorageMembership GetMembership(ItemBase item);
    StorageTransitionPlan MoveTo(ItemBase item, StorageMembership target);
    void ProjectLostAndFound(ItemBase item);
    void RestoreLostAndFound(ItemBase item);
}

internal sealed class DerailValleyStorageRuntimeAdapter : IStorageRuntimeAdapter
{
    public StorageMembership GetMembership(ItemBase item)
    {
        StorageController storage = StorageController.Instance;
        if (item == null || storage == null)
            return StorageMembership.None;
        StorageMembership membership = StorageMembership.None;
        if (storage.StorageInventory?.ContainsItem(item) == true)
            membership |= StorageMembership.Inventory;
        if (storage.StorageWorld?.ContainsItem(item) == true)
            membership |= StorageMembership.World;
        if (storage.StorageLostAndFound?.ContainsItem(item) == true)
            membership |= StorageMembership.LostAndFound;
        if (storage.StorageItemContainers?.ContainsItem(item) == true)
            membership |= StorageMembership.ItemContainer;
        return membership;
    }

    public StorageTransitionPlan MoveTo(ItemBase item, StorageMembership target)
    {
        StorageController storage = StorageController.Instance;
        RuntimeStorageView view = new(storage != null, GetMembership(item));
        StorageTransitionPlan plan = StorageTransitionPlanner.Plan(view, target);
        if (!plan.Accepted || item == null || storage == null)
            return plan;

        Remove(plan.Remove, storage, item);
        Add(plan.Add, storage, item);
        return plan;
    }

    public void ProjectLostAndFound(ItemBase item)
    {
        if (item == null)
            return;
        try { item.ForceEndInteraction(); } catch { }
        MoveTo(item, StorageMembership.LostAndFound);
        Rigidbody body = item.ItemRigidbody;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }
        item.InteractionAllowed = false;
        item.transform.SetParent(WorldMover.OriginShiftParent, true);
        item.gameObject.SetActive(false);
    }

    public void RestoreLostAndFound(ItemBase item)
    {
        if (item == null)
            return;
        MoveTo(item, StorageMembership.None);
        item.gameObject.SetActive(true);
        item.InteractionAllowed = true;
    }

    private static void Remove(StorageMembership remove, StorageController storage, ItemBase item)
    {
        if ((remove & StorageMembership.ItemContainer) != 0)
            storage.RemoveItemFromStorageItemContainers(item);
        if ((remove & StorageMembership.Inventory) != 0)
            storage.RemoveItemFromStorageItemList(storage.StorageInventory, item);
        if ((remove & StorageMembership.World) != 0)
            storage.RemoveItemFromStorageItemList(storage.StorageWorld, item);
        if ((remove & StorageMembership.LostAndFound) != 0)
            storage.RemoveItemFromStorageItemList(storage.StorageLostAndFound, item);
    }

    private static void Add(StorageMembership add, StorageController storage, ItemBase item)
    {
        if ((add & StorageMembership.Inventory) != 0)
            storage.AddItemToStorageItemList(storage.StorageInventory, item.gameObject);
        if ((add & StorageMembership.World) != 0)
            storage.AddItemToWorldStorage(item);
        if ((add & StorageMembership.LostAndFound) != 0)
            storage.AddItemToStorageItemList(storage.StorageLostAndFound, item.gameObject);
        if ((add & StorageMembership.ItemContainer) != 0)
            storage.AddItemToStorageItemList(storage.StorageItemContainers, item.gameObject);
    }

    private sealed class RuntimeStorageView : IStorageView
    {
        public RuntimeStorageView(bool available, StorageMembership membership)
        {
            IsAvailable = available;
            Membership = membership;
        }

        public bool IsAvailable { get; }
        public StorageMembership Membership { get; }
    }
}

internal static class StorageIntegration
{
    private static readonly IStorageRuntimeAdapter runtime =
        new DerailValleyStorageRuntimeAdapter();

    public static StorageMembership GetMembership(ItemBase item) => runtime.GetMembership(item);
    public static StorageTransitionPlan MoveTo(ItemBase item, StorageMembership target) =>
        runtime.MoveTo(item, target);
    public static void ProjectLostAndFound(ItemBase item) => runtime.ProjectLostAndFound(item);
    public static void RestoreLostAndFound(ItemBase item) => runtime.RestoreLostAndFound(item);
}
