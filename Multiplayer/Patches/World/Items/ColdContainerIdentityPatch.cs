using System;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Utils;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch]
public static class ColdContainerIdentityPatch
{
    private const string SaveKey = "multiplayerColdContainerId";

    [HarmonyPatch(typeof(AItemContainer), "Awake")]
    [HarmonyPostfix]
    private static void ContainerAwake(AItemContainer __instance)
    {
        if (__instance != null)
            __instance.gameObject.GetOrAddComponent<ColdContainerIdentity>();
    }

    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.SaveItemData))]
    [HarmonyPostfix]
    private static void SaveItemData(ItemSaveData __instance, ref JObject __result)
    {
        ColdContainerIdentity identity = __instance?.GetComponent<ColdContainerIdentity>();
        if (identity == null) return;
        __result ??= new JObject();
        __result[SaveKey] = identity.PersistentId.ToString("D");
    }

    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.LoadItemData))]
    [HarmonyPrefix]
    private static void LoadItemData(ItemSaveData __instance, JObject data)
    {
        if (__instance == null || data == null ||
            !Guid.TryParse((string)data[SaveKey], out Guid id) || id == Guid.Empty)
            return;
        __instance.gameObject.GetOrAddComponent<ColdContainerIdentity>().Restore(id);
    }
}
