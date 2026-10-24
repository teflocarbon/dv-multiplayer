using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// An item's id rides in its own state (<see cref="ItemSaveData"/>), so an item vanilla makes from its
/// own lists (an installed gadget) comes back with it. Nothing else is written there: the owner is the
/// ledger's (docs/design/items/save-load.md).
/// </summary>
[HarmonyPatch]
internal static class ItemIdentityPatch
{
    internal const string Key = "dvmpPersistentWorldItemId";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.SaveItemData))]
    private static void Save(ItemSaveData __instance, ref JObject __result)
    {
        ItemIdentity identity = __instance?.GetComponent<ItemIdentity>();
        if (identity == null || identity.Value == Guid.Empty)
            return;
        __result ??= new JObject();
        __result[Key] = identity.Value.ToString("D");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.LoadItemData))]
    private static void Load(ItemSaveData __instance, JObject data)
    {
        if (__instance == null || !Guid.TryParse((string)data?[Key], out Guid id) || id == Guid.Empty)
            return;
        ItemIdentity identity = __instance.GetComponent<ItemIdentity>() ??
                                               __instance.gameObject.AddComponent<ItemIdentity>();
        identity.Value = id;
    }
}
