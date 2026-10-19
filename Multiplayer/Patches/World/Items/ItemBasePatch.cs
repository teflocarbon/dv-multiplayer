using DV.CabControls;
using HarmonyLib;
using Multiplayer.Components.Networking.World;
using Multiplayer.Utils;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch(typeof(ItemBase))]
public static class ItemBase_Patch
{
    [HarmonyPatch(nameof(ItemBase.Awake))]
    [HarmonyPostfix]
    private static void Awake(ItemBase __instance)
    {
        //Multiplayer.Log($"ItemBase.Awake() ItemSpec: {__instance?.InventorySpecs?.itemPrefabName}");
        var networkedItem = __instance.GetOrAddComponent<NetworkedItem>();

        //networkedItem.FinaliseTrackedValues();
        return;
    }

    [HarmonyPatch(nameof(ItemBase.OnEnable))]
    [HarmonyPostfix]
    private static void OnEnable(ItemBase __instance)
    {
        ResetAccelerationCooldown(__instance.GetComponent<ItemAccelerationReaction>());
    }

    // VR flick reactions block themselves with a flag that a coroutine on the reaction clears after
    // a short cooldown. Deactivating the item inside that window kills the coroutine and leaves the
    // flag set for good (the lighter never flicks open or shut again, the wallet never drops another
    // coin). Multiplayer toggles item GameObjects far more often than vanilla, so clear it on enable,
    // when no coroutine on the reaction can still be running.
    private static void ResetAccelerationCooldown(ItemAccelerationReaction reaction)
    {
        switch (reaction)
        {
            case LighterAccelerationReaction lighter:
                lighter.suspendReaction = false;
                lighter.SuspendReactionCoro = null;
                break;
            case WalletAccelerationReaction wallet:
                wallet.canSpawnCoin = true;
                wallet.spawnCoro = null;
                break;
        }
    }
}
