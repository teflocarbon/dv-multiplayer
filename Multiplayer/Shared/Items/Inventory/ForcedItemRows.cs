using DV;
using DV.CabControls;
using DV.Items;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// An item vanilla keeps near its place (<c>ItemLocationForcer</c>: farther than its distance, a
/// fast travel, and it's dropped and put back) when the local player stashes it (D14,
/// OI-COLD-11). Vanilla's forcer holds the item's object, which a put-away destroys, so it would
/// let the item be carried off as a row. The forcer follows the item by its NetId instead: while
/// it's a stand-in in the local inventory, the stashed item is where the player's camera is (as
/// vanilla's <c>ItemPositionHandler</c> has it), and once that is too far the item is taken out
/// and the forcer puts it back as vanilla would. An object of it made later (a take-out, the
/// world's) is the forcer's again.
/// </summary>
[HarmonyPatch(typeof(ItemLocationForcer))]
internal static class ForcedItemRows
{
    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    private static void AfterStart(ItemLocationForcer __instance)
    {
        if (__instance != null && __instance.itemGO != null)
            __instance.gameObject.AddComponent<ForcedItem>().Forcer = __instance;
    }

    [HarmonyPatch("OnAboutToFastTravel")]
    [HarmonyPrefix]
    private static void BeforeFastTravel(ItemLocationForcer __instance) =>
        __instance.GetComponent<ForcedItem>()?.TakeOutIfStashed("forced-item-fast-travel");

    private sealed class ForcedItem : MonoBehaviour
    {
        private const float CheckSeconds = 1f;

        public ItemLocationForcer Forcer;
        private ushort netId;
        private float nextCheck;

        private void Update()
        {
            if (Forcer == null || Time.time < nextCheck)
                return;
            nextCheck = Time.time + CheckSeconds;
            if (Forcer.itemGO != null)
            {
                if (netId == 0 && Forcer.itemGO.TryGetComponent(out NetworkedItem networked))
                    netId = networked.NetId;
                return;
            }
            if (netId == 0)
                return;
            if (NetworkedItem.TryGet(netId, out NetworkedItem made) && made != null)
            {
                Rebind(made);
                return;
            }
            Camera camera = PlayerManager.ActiveCamera;
            if (camera != null && Forcer.forcedLocation != null && Forcer.isActiveAndEnabled &&
                (camera.transform.position - Forcer.forcedLocation.position).sqrMagnitude > Forcer.allowedSqrDistance)
                TakeOutIfStashed("forced-item-too-far");
        }

        // The stashed item is taken out for the forcer, which drops it and puts it back.
        public void TakeOutIfStashed(string reason)
        {
            if (netId == 0 || Forcer.itemGO != null || !InventoryStandIn.TryGet(netId, out InventoryStandIn standIn) ||
                InventorySilhouettes.IsSilhouette(standIn) || InventoryRowHolder.Current is not { } holder)
                return;
            if (holder.TakeOut(standIn, reason) is not { } item)
                return;
            Rebind(item);
            Forcer.StartCoroutine(Forcer.ForceItemStartLocation());
        }

        // The forcer's object is this one now, and so is its position.
        private void Rebind(NetworkedItem item)
        {
            Forcer.itemGO = item.gameObject;
            Forcer.item = item.GetComponent<ItemBase>();
            if (Forcer.itemPositionHandler != null)
                Destroy(Forcer.itemPositionHandler);
            Forcer.itemPositionHandler = Forcer.gameObject.AddComponent<ItemPositionHandler>();
            Forcer.itemPositionHandler.Initialize(Forcer.item);
        }
    }
}
