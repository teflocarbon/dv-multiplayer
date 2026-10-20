using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Server.Items;

[HarmonyPatch]
internal static class PersistentWorldItemIdentityPatch
{
    internal const string Key = "dvmpPersistentWorldItemId";
    internal const string ManagedWorldKey = "dvmpManagedWorldItem";
    internal const string AuthoredKey = "dvmpAuthoredWorldItemKey";
    internal const string OwnerKey = "dvmpPersistentOwner";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemSaveData), nameof(ItemSaveData.SaveItemData))]
    private static void Save(ItemSaveData __instance, ref JObject __result)
    {
        PersistentWorldItemIdentity identity = __instance?.GetComponent<PersistentWorldItemIdentity>();
        NetworkedItem networked = __instance?.GetComponent<NetworkedItem>();
        bool managedWorldItem = networked != null && ItemServer.Current?.OwnsWorldPersistence(networked) == true;
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
        // An installed gadget is saved only in vanilla's installed storage, so its owner goes there
        // too (gadgets.md, "Persistence").
        if (networked != null && AuthoritativeItemRegistry.TryGet(networked.NetId,
                out AuthoritativeItemRegistry.Record record) &&
            record.Placement == ItemPlacementKind.Installed && record.PersistentOwnerIdentity != Guid.Empty)
            __result[OwnerKey] = record.PersistentOwnerIdentity.ToString("D");
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
        if (Guid.TryParse((string)data[OwnerKey], out Guid owner))
            identity.Owner = owner;
    }
}
