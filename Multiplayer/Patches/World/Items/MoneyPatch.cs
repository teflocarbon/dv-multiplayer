using System.Globalization;
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

    /// <summary>
    /// Every way a client uses up a note or coin (inventory pickup, stashing it on the wallet,
    /// paying a cash register) spends it here. The wallet is shared and owned by the host, which
    /// never heard about these: the value was credited only to the client's mirrored balance
    /// (overwritten by the host's next update) and the note survived on the host. Deposit it
    /// with the host first; the local flow then carries on and the host's balance corrects ours.
    /// </summary>
    [HarmonyPatch(nameof(Money.TrySpend))]
    [HarmonyPrefix]
    private static void TrySpend(Money __instance)
    {
        if (__instance.Amount <= double.Epsilon || NetworkLifecycle.Instance == null ||
            NetworkLifecycle.Instance.IsHost() || !NetworkLifecycle.Instance.IsClientRunning)
            return;
        if (__instance.TryGetComponent(out NetworkedItem networked) && networked.NetId != 0)
            NetworkLifecycle.Instance.Client.SendMoneyItemDeposit(networked.NetId);
    }
}
