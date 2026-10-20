using Multiplayer.Core.Career;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Career;

[TestFixture]
public sealed class FeePaymentPlanTests
{
    [Test]
    public void WithoutInsuranceEveryFeeIsPaid()
    {
        var (count, cost) = FeePaymentPlan.Choose(new[] { 10.0, 20.0, 300.0 },
            insuranceUsed: false, quotaReached: false, leftToReachQuota: 0);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(cost, Is.EqualTo(330.0));
    }

    [Test]
    public void WithInsuranceTheCheapestArePaidUntilTheQuotaIsReached()
    {
        // 10 + 20 = 30 is still under 40, 10 + 20 + 50 reaches it; the 300 is left for insurance.
        var (count, cost) = FeePaymentPlan.Choose(new[] { 10.0, 20.0, 50.0, 300.0 },
            insuranceUsed: true, quotaReached: false, leftToReachQuota: 40);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(cost, Is.EqualTo(80.0));
    }

    [Test]
    public void WithInsuranceAFeeThatReachesTheQuotaExactlyIsTheLastPaid()
    {
        var (count, cost) = FeePaymentPlan.Choose(new[] { 15.0, 25.0, 60.0 },
            insuranceUsed: true, quotaReached: false, leftToReachQuota: 40);

        Assert.That(count, Is.EqualTo(2));
        Assert.That(cost, Is.EqualTo(40.0));
    }

    [Test]
    public void WithInsuranceAndTheQuotaAlreadyReachedNothingIsPaid()
    {
        var (count, cost) = FeePaymentPlan.Choose(new[] { 10.0, 20.0 },
            insuranceUsed: true, quotaReached: true, leftToReachQuota: 0);

        Assert.That(count, Is.Zero);
        Assert.That(cost, Is.Zero);
    }

    [Test]
    public void WithInsuranceAndFeesUnderTheQuotaAllArePaid()
    {
        var (count, cost) = FeePaymentPlan.Choose(new[] { 10.0, 20.0 },
            insuranceUsed: true, quotaReached: false, leftToReachQuota: 1000);

        Assert.That(count, Is.EqualTo(2));
        Assert.That(cost, Is.EqualTo(30.0));
    }

    [Test]
    public void NoFeesNothingToPay()
    {
        Assert.That(FeePaymentPlan.Choose(new double[0], false, false, 0), Is.EqualTo((0, 0.0)));
        Assert.That(FeePaymentPlan.Choose(null, true, false, 10), Is.EqualTo((0, 0.0)));
    }
}
