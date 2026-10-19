using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using DV.ServicePenalty;

namespace Multiplayer.Networking.Managers.Server;

/// <summary>
/// Pays the fees from the shared wallet the way a player at the career manager's fees screen would,
/// for /payfees and the fees screen on a client (ChatManager.PayFees).
///
/// With insurance, the fees screen clears every remaining fee free once the fees paid reach the
/// insurance quota (CareerManagerFeesScreen.Activate). So this pays the cheapest fees first, only
/// until the quota is reached, and lets insurance clear the rest; without insurance it pays them all.
/// </summary>
internal static class FeePayment
{
    public readonly struct Result
    {
        public readonly int Paid;
        public readonly double Cost;
        public readonly int Cleared;
        /// <summary>When the wallet was short: what paying would have cost. Nothing was paid.</summary>
        public readonly double Shortfall;

        public Result(int paid, double cost, int cleared, double shortfall)
        {
            Paid = paid;
            Cost = cost;
            Cleared = cleared;
            Shortfall = shortfall;
        }

        public bool NothingToPay => Paid == 0 && Cleared == 0 && Shortfall == 0;
    }

    public static Result PayAll()
    {
        var debts = CareerManagerDebtController.Instance;
        InsuranceFeeQuota quota = debts.feeQuota;
        debts.RefreshExistingDebtsState();
        debts.SortNonZeroDebtsAscending();

        List<DisplayableDebt> toPay = [];
        double cost = 0;
        if (!(quota.InsuranceUsed && quota.QuotaReached))
        {
            foreach (DisplayableDebt debt in debts.currentNonZeroPricedDebts.Where(debt => debt.IsPayable))
            {
                if (quota.InsuranceUsed && cost >= quota.LeftToReachQuota)
                    break;
                toPay.Add(debt);
                cost += debt.GetTotalPrice();
            }
        }

        if (toPay.Count > 0)
        {
            if (Inventory.Instance.PlayerMoney < cost || !Inventory.Instance.RemoveMoney(cost))
                return new Result(0, 0, 0, cost);
            foreach (DisplayableDebt debt in toPay)
                debt.Pay();
            debts.RefreshExistingDebtsState();
        }

        int cleared = 0;
        if (quota.InsuranceUsed && quota.QuotaReached && !debts.HasAnyUnpayableFees)
        {
            cleared = debts.currentNonZeroPricedDebts.Count(debt => debt.IsPayable);
            debts.ClearDebtsViaInsuranceQuotaReached();
            debts.RefreshExistingDebtsState();
        }
        return new Result(toPay.Count, cost, cleared, 0);
    }
}
