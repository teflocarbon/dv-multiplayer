using System.Globalization;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Utils;

namespace Multiplayer.Shared.Items.Behaviours;

/// <summary>
/// Banknotes and coins. Their value lives on the item, which the item sync never carried, so a
/// client's copy of a $1,234 job payout was worth vanilla's default $10. Replicate it as a
/// host-authoritative tracked value (a string, so large amounts survive exactly). A client's spend
/// is <c>Client/Items/Behaviours/MoneyPatch</c>.
/// </summary>
[HarmonyPatch(typeof(Money), nameof(Money.Awake))]
public static class MoneyAmountPatch
{
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
