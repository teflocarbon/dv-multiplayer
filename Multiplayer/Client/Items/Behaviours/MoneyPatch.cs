using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Items.Behaviours;

/// <summary>
/// A client spending a banknote or coin. Its amount is synced by
/// <c>Shared/Items/Behaviours/MoneyAmountPatch</c>.
/// </summary>
[HarmonyPatch(typeof(Money))]
public static class MoneyPatch
{
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
            !NetworkRole.IsRemoteClient)
            return;
        if (__instance.TryGetComponent(out NetworkedItem networked) && networked.NetId != 0)
            NetworkClient.Instance.SendItemRequest(new ItemRequest
            {
                ItemNetId = networked.NetId,
                ExpectedRevision = networked.AuthorityRevision,
                Action = ItemAction.DepositMoney
            });
    }
}
