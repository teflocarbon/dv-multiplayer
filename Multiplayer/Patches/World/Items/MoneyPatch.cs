using System;
using System.Globalization;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// Banknotes and coins. Their value lives on the item, which the item sync never carried, so a
/// client's copy of a $1,234 job payout was worth vanilla's default $10. Replicate it as a
/// host-authoritative tracked value (a string, so large amounts survive exactly).
/// </summary>
[HarmonyPatch(typeof(Money))]
public static class MoneyPatch
{
    [HarmonyPatch(nameof(Money.Awake))]
    [HarmonyPostfix]
    private static void Awake(Money __instance)
    {
        NetworkedItem netItem = __instance.GetOrAddComponent<NetworkedItem>();
        Money money = __instance;
        netItem.RegisterTrackedValue(
            "amount",
            () => money.Amount.ToString("R", CultureInfo.InvariantCulture),
            value =>
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
                    money.Amount = amount;
            },
            serverAuthoritative: true);
        netItem.FinaliseTrackedValues();
    }
}

/// <summary>
/// Vanilla spends a money item into the local wallet the moment it enters the inventory. The
/// wallet is shared and owned by the host (clients only mirror its balance), so on a client
/// that deposit was overwritten by the next balance update and the note survived on the host.
/// Hand the deposit to the host instead; the local copy is still consumed as normal.
/// </summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItemToInventory),
    [typeof(GameObject), typeof(int), typeof(bool), typeof(bool)])]
public static class InventoryMoneyDepositPatch
{
    [HarmonyPrefix]
    private static void Prefix(Inventory __instance, GameObject item, int slot)
    {
        if (item == null || NetworkLifecycle.Instance == null || NetworkLifecycle.Instance.IsHost() ||
            !NetworkLifecycle.Instance.IsClientRunning)
            return;
        if (!item.TryGetComponent(out IMoney money) || !money.ShouldDestroyOnUse)
            return;
        if (!InventoryUtils.IsValidInventoryIndex(slot) || __instance.Contains(item, includeDropped: false))
            return; // vanilla rejects these before spending

        // Zero the local copy so vanilla doesn't credit the mirrored wallet itself.
        money.TrySpend(money.Amount);
        if (item.TryGetComponent(out NetworkedItem networked) && networked.NetId != 0)
            NetworkLifecycle.Instance.Client.SendMoneyItemDeposit(networked.NetId);
    }
}
