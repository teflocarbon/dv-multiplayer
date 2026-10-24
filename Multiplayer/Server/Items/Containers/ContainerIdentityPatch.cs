using System;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Shared.Utils;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Server.Items.Containers;

[HarmonyPatch]
public static class ContainerIdentityPatch
{
    internal const string SaveKey = "multiplayerColdContainerId";

    [HarmonyPatch(typeof(AItemContainer), "Awake")]
    [HarmonyPostfix]
    private static void ContainerAwake(AItemContainer __instance)
    {
        if (__instance != null)
            __instance.gameObject.GetOrAddComponent<ContainerIdentity>();
    }

    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.SaveItemData))]
    [HarmonyPostfix]
    private static void SaveItemData(ItemSaveData __instance, ref JObject __result)
    {
        ContainerIdentity identity = __instance?.GetComponent<ContainerIdentity>();
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
        __instance.gameObject.GetOrAddComponent<ContainerIdentity>().Restore(id);
    }
}
