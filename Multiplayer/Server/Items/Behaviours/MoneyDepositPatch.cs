using HarmonyLib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items.Behaviours;

/// <summary>
/// The host's own player spends a banknote or coin (OI-LIN-05; docs/design/items/ledger.md, "Births
/// and ends"): vanilla's inventory, wallet or register takes its value and destroys it, which ended its
/// entry <c>DestroyedFromInventory</c>. Its end is <c>MoneyDeposited</c> by them, as a client's
/// deposit request's is (<c>NetworkServer.TrySpendMoneyItem</c>, whose own note comes after this one).
/// </summary>
[HarmonyPatch(typeof(Money), nameof(Money.TrySpend))]
internal static class MoneyDepositPatch
{
    [HarmonyPostfix]
    private static void Postfix(Money __instance, double __result)
    {
        if (__result <= double.Epsilon || NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsSessionRunning ||
            !NetworkLifecycle.Instance.IsHost() || !__instance.TryGetComponent(out NetworkedItem item) || item.NetId == 0)
            return;
        item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.MoneyDeposited, NetworkServer.Instance?.LocalPlayer,
            $"${__result:N2} spent"));
    }
}
