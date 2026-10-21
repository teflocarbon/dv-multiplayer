using System.Collections.Generic;
using DV.CabControls;
using DV.CashRegister;
using DV.Interaction;
using DV.Shops;
using HarmonyLib;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Shops;
using UnityEngine;

namespace Multiplayer.Client.Shops;

/// <summary>
/// What a player does at a shop register, on any game (the host's included), becomes a request to
/// the server (<see cref="ShopsClient"/>): scanning, paying with the wallet or a banknote, Buy and
/// Cancel. Vanilla doesn't change the register here; the server's state comes back and
/// <see cref="ShopRegisterView"/> shows it (docs/design/shops.md). Service-station registers are
/// not shops and keep their own path (<c>Client/Stations</c>).
/// </summary>
[HarmonyPatch]
public static class ShopRegisterPatches
{
    // A scan: one more of the item in the basket, owned by this player. True keeps the scanner's
    // scan sound; the line shows when the server's state comes back.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScanItemCashRegisterModule), nameof(ScanItemCashRegisterModule.AddItemsToBuy))]
    private static bool AddItemsToBuy(ScanItemCashRegisterModule __instance, ref bool __result)
    {
        if (!ShopRegisters.ServerOwned)
            return true;
        __result = ShopRegisters.TryLocate(__instance, out int shop, out int module);
        if (__result)
            ShopsClient.Current?.SendShopScan((byte)shop, (byte)module);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(CashRegisterWithModules), nameof(CashRegisterWithModules.OnBuyPressed))]
    private static bool OnBuyPressed(CashRegisterWithModules __instance)
    {
        if (!ShopRegisters.ServerOwned || !ShopRegisters.TryGetShopIndex(__instance, out int shop))
            return true;
        ShopsClient.Current?.SendShopBuy((byte)shop, ShopRegisterView.Revision(shop));
        return false;
    }

    // The Cancel button (vanilla's OnDisable cancel is off for shop registers: CashRegisterWithModulesPatch).
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(CashRegisterWithModules), nameof(CashRegisterWithModules.Cancel))]
    private static bool Cancel(CashRegisterWithModules __instance)
    {
        if (!ShopRegisters.ServerOwned || !ShopRegisters.TryGetShopIndex(__instance, out int shop))
            return true;
        ShopsClient.Current?.SendShopCancel((byte)shop);
        return false;
    }

    // Using the wallet (what is still owed) or a banknote (all of it) on the register.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MoneyUse), nameof(MoneyUse.HandleUse))]
    private static bool HandleUse(MoneyUse __instance, ItemUseTarget target, ref bool __result)
    {
        if (!ShopRegisters.ServerOwned || target == null || !target.TryGetComponent(out CashRegisterBase register) ||
            !ShopRegisters.TryGetShopIndex(register, out int shop))
            return true;
        __result = Pay(shop, __instance.GetComponent<IMoney>(), __instance.GetComponent<ItemBase>());
        return false;
    }

    // A banknote pushed into the register while held.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CashRegisterBase), nameof(CashRegisterBase.OnTriggerEnter))]
    private static bool OnTriggerEnter(CashRegisterBase __instance, Collider col)
    {
        if (!ShopRegisters.ServerOwned || !ShopRegisters.TryGetShopIndex(__instance, out int shop))
            return true;
        IMoney money = col.GetComponentInParent<IMoney>();
        ItemBase item = col.GetComponentInParent<ItemBase>();
        if (money != null && item != null && item.IsGrabbed() && money.ShouldDestroyOnUse)
            Pay(shop, money, item);
        return false;
    }

    // One request per banknote: its colliders can enter the trigger several times before it's gone.
    private static readonly HashSet<ushort> notesSent = new();

    private static bool Pay(int shop, IMoney money, ItemBase item)
    {
        if (money == null)
            return false;
        ushort note = 0;
        if (money.ShouldDestroyOnUse)
        {
            if (!NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem networked) || networked == null || networked.NetId == 0)
                return false;
            note = networked.NetId;
            if (!notesSent.Add(note))
                return true;
        }
        ShopsClient.Current?.SendShopDeposit((byte)shop, note);
        return true;
    }

    public static void Reset() => notesSent.Clear();
}
