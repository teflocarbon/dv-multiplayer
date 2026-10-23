using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using DV.CashRegister;
using DV.Interaction;
using DV.Shops;
using HarmonyLib;
using Multiplayer.Core.Shops;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Shops;
using UnityEngine;

namespace Multiplayer.Client.Shops;

/// <summary>
/// What a player does at a shop register, on any game (the host's included), becomes a request to
/// the server (<see cref="ShopsClient"/>): scanning, paying with the wallet or a banknote, Buy and
/// Cancel. Vanilla doesn't change the register here: each request is shown at once as this player's
/// prediction (<see cref="ShopRegisterView.Predict"/>), and the server's state, or its refusal,
/// replaces it a round trip later (docs/design/shops.md). Service-station registers are not shops
/// and keep their own path (<c>Client/Stations</c>).
/// </summary>
[HarmonyPatch]
public static class ShopRegisterPatches
{
    // A scan: one more of the item in the basket, owned by this player, shown at once
    // (ShopRegisterView.Predict). True keeps the scanner's scan sound; false its error sound, when
    // this game can already see it's out of stock, as vanilla's own check did.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScanItemCashRegisterModule), nameof(ScanItemCashRegisterModule.AddItemsToBuy))]
    private static bool AddItemsToBuy(ScanItemCashRegisterModule __instance, ref bool __result)
    {
        if (!ShopRegisters.ServerOwned)
            return true;
        __result = false;
        if (!ShopRegisters.TryLocate(__instance, out int shop, out int module))
            return false;
        ShopItemData data = GlobalShopController.Instance.GetShopItemData(__instance.sellingItemSpec);
        int inBasket = ShopRegisterView.Shown(shop).Lines
            .Where(l => ShopRegisters.TryGetModule(shop, l.Module, out ScanItemCashRegisterModule m) && m.sellingItemSpec == __instance.sellingItemSpec)
            .Sum(l => l.Units);
        if (data == null || inBasket >= data.ItemsInStock)
            return false;
        uint id = ShopRegisterView.Predict(shop, ShopRequestKind.Scan, module);
        ShopsClient.Current?.SendShopScan(id, (byte)shop, (byte)module);
        __result = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(CashRegisterWithModules), nameof(CashRegisterWithModules.OnBuyPressed))]
    private static bool OnBuyPressed(CashRegisterWithModules __instance)
    {
        if (!ShopRegisters.ServerOwned || !ShopRegisters.TryGetShopIndex(__instance, out int shop))
            return true;
        bool wasDelivering = ShopRegisterView.Shown(shop).Delivering;
        uint id = ShopRegisterView.Predict(shop, ShopRequestKind.Buy);
        // The sale as it will happen; if the server refuses it, its not-enough-money sound follows.
        if (!wasDelivering && ShopRegisterView.Shown(shop).Delivering)
            ShopRegisterView.PlayBuy(__instance);
        ShopsClient.Current?.SendShopBuy(id, (byte)shop, ShopRegisterView.Revision(shop));
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
        // Vanilla plays the cancel sound only when there was cash to hand back.
        if (ShopRegisterView.Shown(shop).Deposited > 0)
            ShopRegisterView.PlayAtRegister(__instance, __instance.cancelAudio);
        uint id = ShopRegisterView.Predict(shop, ShopRequestKind.Cancel);
        ShopsClient.Current?.SendShopCancel(id, (byte)shop);
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

    // Shown at once, with the add-cash sound: the wallet pays what is still owed (or what it has), a
    // banknote all of it. Nothing is paid while nothing is owed, as in vanilla.
    private static bool Pay(int shop, IMoney money, ItemBase item)
    {
        if (money == null)
            return false;
        double owed = ShopRegisterView.Shown(shop).Owed(module =>
            ShopRegisters.TryGetModule(shop, module, out ScanItemCashRegisterModule m) ? m.Data.pricePerUnit : 0);
        if (owed <= 0)
            return false;
        ushort note = 0;
        double amount;
        if (money.ShouldDestroyOnUse)
        {
            if (!NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem networked) || networked == null || networked.NetId == 0)
                return false;
            note = networked.NetId;
            if (!notesSent.Add(note))
                return true;
            amount = money.Amount;
        }
        else
            amount = System.Math.Min(owed, money.Amount);
        if (amount <= 0)
            return false;
        uint id = ShopRegisterView.Predict(shop, ShopRequestKind.Deposit, amount: amount, cashSound: true);
        ShopsClient.Current?.SendShopDeposit(id, (byte)shop, note);
        return true;
    }

    public static void Reset() => notesSent.Clear();
}
