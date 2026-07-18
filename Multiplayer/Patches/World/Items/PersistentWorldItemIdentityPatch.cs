using HarmonyLib;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.Networking.World.WorldItems;
using Newtonsoft.Json.Linq;
using System;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch]
internal static class PersistentWorldItemIdentityPatch
{
    internal const string Key = "dvmpPersistentWorldItemId";
    internal const string ManagedWorldKey = "dvmpManagedWorldItem";
    internal const string AuthoredKey = "dvmpAuthoredWorldItemKey";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.SaveItemData))]
    private static void Save(ItemSaveData __instance, ref JObject __result)
    {
        PersistentWorldItemIdentity identity = __instance?.GetComponent<PersistentWorldItemIdentity>();
        NetworkedItem networked = __instance?.GetComponent<NetworkedItem>();
        bool managedWorldItem = networked != null && WorldItemPersistenceManager.OwnsWorldPersistence(networked);
        if (!managedWorldItem && (identity == null || identity.Value == Guid.Empty))
            return;
        __result ??= new JObject();
        if (identity != null && identity.Value != Guid.Empty)
            __result[Key] = identity.Value.ToString("D");
        if (managedWorldItem || networked?.IsSceneAuthored == true)
        {
            __result[ManagedWorldKey] = true;
            if (networked.IsSceneAuthored)
                __result[AuthoredKey] = networked.AuthoredItemKey;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.LoadItemData))]
    private static void Load(ItemSaveData __instance, JObject data)
    {
        if (__instance == null || !Guid.TryParse((string)data?[Key], out Guid id) || id == Guid.Empty)
            return;
        PersistentWorldItemIdentity identity = __instance.GetComponent<PersistentWorldItemIdentity>() ??
                                               __instance.gameObject.AddComponent<PersistentWorldItemIdentity>();
        identity.Value = id;
    }
}
