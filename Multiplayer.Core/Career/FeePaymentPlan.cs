using System.Collections.Generic;

namespace Multiplayer.Core.Career;

/// <summary>
/// Which fees to pay when paying them all, the way a player at the career manager's fees screen
/// would. With insurance, the fees screen clears every remaining fee free once the fees paid reach
/// the insurance quota, so only the cheapest are paid, until the quota is reached. Without
/// insurance, all of them are.
/// </summary>
public static class FeePaymentPlan
{
    /// <summary>
    /// How many of the payable fees to pay, cheapest first, and what that costs.
    /// </summary>
    /// <param name="payablePricesCheapestFirst">The price of each payable fee, cheapest first.</param>
    /// <param name="insuranceUsed">The career has insurance.</param>
    /// <param name="quotaReached">The fees paid have already reached the insurance quota.</param>
    /// <param name="leftToReachQuota">What's left to pay before the quota is reached.</param>
    public static (int Count, double Cost) Choose(IReadOnlyList<double> payablePricesCheapestFirst,
        bool insuranceUsed, bool quotaReached, double leftToReachQuota)
    {
        if (payablePricesCheapestFirst == null || (insuranceUsed && quotaReached))
            return (0, 0);

        int count = 0;
        double cost = 0;
        foreach (double price in payablePricesCheapestFirst)
        {
            if (insuranceUsed && cost >= leftToReachQuota)
                break;
            count++;
            cost += price;
        }
        return (count, cost);
    }
}
