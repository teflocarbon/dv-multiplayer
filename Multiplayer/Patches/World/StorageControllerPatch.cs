using DV.CabControls;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Core.Items;

namespace Multiplayer.Patches.World;

internal static class MultiplayerStoragePatchGuard
{
    internal static bool Active => NetworkLifecycle.Instance != null &&
        (NetworkLifecycle.Instance.IsServerRunning || NetworkLifecycle.Instance.IsClientRunning);

    internal static bool IsManaged(ItemBase item, out NetworkedItem networked)
    {
        networked = null;
        if (!Active || item == null || !NetworkedItem.TryGetNetworkedItem(item, out networked) ||
            networked == null || networked.NetId == 0)
            return false;
        if (NetworkLifecycle.Instance.IsHost() &&
            AuthoritativeItemRegistry.TryGet(networked.NetId, out AuthoritativeItemRegistry.Record authority))
            return authority.PersistentOwnerPlayerId != 0;
        return networked.PersistentOwnerPlayerId != 0;
    }
}

[HarmonyPatch(typeof(StorageController), nameof(StorageController.AddItemToLostAndFound))]
internal static class StorageControllerAddLostItemPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ItemBase item)
    {
        if (!MultiplayerStoragePatchGuard.IsManaged(item, out NetworkedItem networked))
            return true;
        if (NetworkLifecycle.Instance.IsHost())
            NetworkedLostAndFoundManager.Collect(networked, LostItemReason.SaveRecovery);
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
