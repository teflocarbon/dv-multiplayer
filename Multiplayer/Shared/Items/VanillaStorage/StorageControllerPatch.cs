using DV.CabControls;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items.VanillaStorage;

internal static class MultiplayerStoragePatchGuard
{
    internal static bool Active => NetworkLifecycle.Instance != null &&
        NetworkLifecycle.Instance.IsSessionRunning;

    internal static bool IsManaged(ItemBase item, out NetworkedItem networked)
    {
        networked = null;
        if (!Active || item == null || !NetworkedItem.TryGetNetworkedItem(item, out networked) ||
            networked == null || networked.NetId == 0)
            return false;
        if (ItemServer.Current?.TryGetPersistentOwner(networked.NetId, out System.Guid owner) == true)
            return owner != System.Guid.Empty;
        return networked.Relation.IsOwned();
    }
}

[HarmonyPatch(typeof(StorageController), "OnInventoryStatusChanged")]
internal static class StorageControllerInventoryStatusPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !InventoryIntegration.AuthoritativeRemovalActive;
}

[HarmonyPatch(typeof(StorageController), nameof(StorageController.AddItemToStorageItemList),
    typeof(StorageBase), typeof(ItemBase))]
internal static class StorageControllerAddManagedItemPatch
{
    [HarmonyPrefix]
    private static bool Prefix(StorageController __instance, StorageBase storage, ItemBase item)
    {
        // Vanilla takes an item out of its old list before it asks whether the new one takes it, so a
        // refused move left an unowned item in none (H3, OI-ESS-13): it is asked first, and keeps its list.
        if (!MultiplayerStoragePatchGuard.IsManaged(item, out _))
            return !(MultiplayerStoragePatchGuard.Active && storage != null && item?.InventorySpecs is { } unowned &&
                     !unowned.BelongsToPlayer && !storage.acceptsNonEssential);

        if (storage == null)
        {
            global::Multiplayer.Multiplayer.LogWarning(
                $"Cannot add managed item {item.name} to a null DV storage.");
            return false;
        }

        InventoryItemSpec spec = item.InventorySpecs;
        if (spec == null)
        {
            global::Multiplayer.Multiplayer.LogWarning(
                $"Cannot add managed item {item.name} to {storage.name}: missing InventoryItemSpec.");
            return false;
        }

        if (!spec.BelongsToPlayer && !storage.acceptsNonEssential)
            return false;

        // DV finishes every storage insertion by invoking RespawnOnDrop.UpdateSpawnParams().
        // Network-created projections do not necessarily carry that component, and managed
        // items must not run an independent single-player respawn policy even when they do.
        // Preserve DV's storage membership bookkeeping while leaving lifecycle authority with
        // the host's item registry.
        __instance.RemoveItemFromStorageItemList(item);
        storage.AddItem(item);
        return false;
    }
}

[HarmonyPatch(typeof(StorageController), nameof(StorageController.AddItemToLostAndFound))]
internal static class StorageControllerAddLostItemPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemBase item)
    {
        if (!MultiplayerStoragePatchGuard.Active)
            return true;

        // Vanilla calls this from its own paths (a respawn, a shelf, a snap point, a cleared
        // container, a gadget's load, a shop): none may put any item in its list in multiplayer,
        // owned or not (OI-VAN-06). Lost and Found is the server's: LostAndFoundServer.Collect.
        global::Multiplayer.Multiplayer.LogDebug(() =>
            $"Suppressed vanilla Lost and Found insertion for {(item != null ? item.name : "null")}" +
            (NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem networked) && networked != null ? $" (netId {networked.NetId})" : ""));
        return false;
    }
}

[HarmonyPatch(typeof(StorageController), nameof(StorageController.RequestLostAndFoundItemActivation))]
internal static class StorageControllerRequestLostActivationPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !MultiplayerStoragePatchGuard.Active;
}

[HarmonyPatch(typeof(StorageController), nameof(StorageController.MoveItemsFromWorldToLostAndFound))]
internal static class StorageControllerMoveWorldItemsPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !MultiplayerStoragePatchGuard.Active;
}

[HarmonyPatch(typeof(StorageController), nameof(StorageController.ForceSummonAllWorldItemsToLostAndFound))]
internal static class StorageControllerForceSummonPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !MultiplayerStoragePatchGuard.Active;
}

[HarmonyPatch(typeof(StorageController), "OnPlayerInActivationRange")]
internal static class StorageControllerAccessPointPatch
{
    [HarmonyPrefix]
    private static bool Prefix(StorageAccessPointBase __0) =>
        !MultiplayerStoragePatchGuard.Active || __0 == null ||
        __0.AccessPointStorageType != StorageType.LostAndFound;
}

[HarmonyPatch(typeof(StorageItemTransformController), nameof(StorageItemTransformController.ActivateItems))]
internal static class LostAndFoundTransformActivationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(StorageItemTransformController __instance)
    {
        if (!MultiplayerStoragePatchGuard.Active)
            return true;
        return StorageController.Instance == null ||
            __instance != StorageController.Instance.ItemTransformControllerLostAndFound;
    }
}
