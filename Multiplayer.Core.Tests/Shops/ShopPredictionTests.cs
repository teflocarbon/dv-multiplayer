using System.Linq;
using Multiplayer.Core.Shops;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Shops;

[TestFixture]
public sealed class ShopPredictionTests
{
    private const byte Me = 2, Other = 1;
    private static double Price(int module) => module == 0 ? 10 : 25;

    [Test]
    public void PendingScansAndPaymentShowOnTopOfTheServersBasket()
    {
        ShopPrediction prediction = new();
        prediction.Add(new PendingShopRequest(1, ShopRequestKind.Scan, module: 0));
        prediction.Add(new PendingShopRequest(2, ShopRequestKind.Scan, module: 0));
        prediction.Add(new PendingShopRequest(3, ShopRequestKind.Deposit, amount: 20));

        PredictedRegister shown = prediction.Predict(new[] { new ShopBasketLine(1, Other, 1) }, 5, false, Me, Price);

        Assert.That(shown.UnitsOf(0), Is.EqualTo(2));
        Assert.That(shown.UnitsOf(1), Is.EqualTo(1));
        Assert.That(shown.Deposited, Is.EqualTo(25));
        Assert.That(shown.Owed(Price), Is.EqualTo(20));
    }

    [Test]
    public void AnAcknowledgementTakesEveryRequestUpToIt()
    {
        ShopPrediction prediction = new();
        prediction.Add(new PendingShopRequest(4, ShopRequestKind.Scan, module: 0));
        prediction.Add(new PendingShopRequest(5, ShopRequestKind.Scan, module: 0));
        prediction.Add(new PendingShopRequest(6, ShopRequestKind.Scan, module: 1));

        Assert.That(prediction.Acknowledge(5), Is.True);
        Assert.That(prediction.Pending.Select(r => r.Id), Is.EqualTo(new uint[] { 6 }));
    }

    [Test]
    public void ARefusalRollsTheRequestBack()
    {
        ShopPrediction prediction = new();
        prediction.Add(new PendingShopRequest(7, ShopRequestKind.Scan, module: 1));
        Assert.That(prediction.Predict(new ShopBasketLine[0], 0, false, Me, Price).UnitsOf(1), Is.EqualTo(1));

        Assert.That(prediction.Refuse(7), Is.True);
        Assert.That(prediction.Predict(new ShopBasketLine[0], 0, false, Me, Price).UnitsOf(1), Is.Zero);
    }

    [Test]
    public void BuyEmptiesAPaidBasketAndShowsProcessing()
    {
        ShopPrediction prediction = new();
        prediction.Add(new PendingShopRequest(1, ShopRequestKind.Buy));

        PredictedRegister shown = prediction.Predict(new[] { new ShopBasketLine(0, Me, 2) }, 30, false, Me, Price);

        Assert.That(shown.Lines, Is.Empty);
        Assert.That(shown.Deposited, Is.Zero);
        Assert.That(shown.Delivering, Is.True);
    }

    [Test]
    public void BuyWithTooLittleCashChangesNothing()
    {
        ShopPrediction prediction = new();
        prediction.Add(new PendingShopRequest(1, ShopRequestKind.Buy));

        PredictedRegister shown = prediction.Predict(new[] { new ShopBasketLine(1, Me, 1) }, 10, false, Me, Price);

        Assert.That(shown.UnitsOf(1), Is.EqualTo(1));
        Assert.That(shown.Deposited, Is.EqualTo(10));
        Assert.That(shown.Delivering, Is.False);
    }

    [Test]
    public void CancelEmptiesBasketAndCash()
    {
        ShopPrediction prediction = new();
        prediction.Add(new PendingShopRequest(1, ShopRequestKind.Cancel));

        PredictedRegister shown = prediction.Predict(new[] { new ShopBasketLine(0, Other, 1) }, 10, false, Me, Price);

        Assert.That(shown.Lines, Is.Empty);
        Assert.That(shown.Deposited, Is.Zero);
    }

    [Test]
    public void OldRequestsExpire()
    {
        ShopPrediction prediction = new();
        prediction.Add(new PendingShopRequest(1, ShopRequestKind.Scan, module: 0, sentAt: 10f));
        prediction.Add(new PendingShopRequest(2, ShopRequestKind.Scan, module: 0, sentAt: 14f));

        Assert.That(prediction.Expire(16f), Is.True);
        Assert.That(prediction.Pending.Select(r => r.Id), Is.EqualTo(new uint[] { 2 }));
    }

    [Test]
    public void AnotherPlayersChangeIsABasketChange()
    {
        ShopBasket basket = new();
        basket.Add(0, Me);
        uint seen = basket.Revision;
        basket.Add(1, Other);

        Assert.That(basket.ChangedByOthersSince(seen, Me), Is.True);
    }

    [Test]
    public void APlayersOwnChangesSinceDontCount()
    {
        ShopBasket basket = new();
        basket.Add(0, Other);
        uint seen = basket.Revision;
        basket.Add(0, Me);
        basket.Deposit(20, Me);

        Assert.That(basket.ChangedByOthersSince(seen, Me), Is.False);
        Assert.That(basket.ChangedByOthersSince(basket.Revision, Other), Is.False);
        Assert.That(basket.ChangedByOthersSince(seen, Other), Is.True);
    }

    [Test]
    public void TheServersOwnChangesCount()
    {
        ShopBasket basket = new();
        basket.Add(0, Me);
        basket.Deposit(10, Me);
        uint seen = basket.Revision;
        basket.TakeDeposit();

        Assert.That(basket.ChangedByOthersSince(seen, Me), Is.True);
    }

    [Test]
    public void ARevisionTooOldToCheckCounts()
    {
        ShopBasket basket = new();
        basket.Add(0, Me);
        uint seen = basket.Revision;
        for (int i = 0; i < 100; i++)
            basket.Add(0, Me);

        Assert.That(basket.ChangedByOthersSince(seen, Me), Is.True);
        Assert.That(basket.ChangedByOthersSince(basket.Revision + 1, Me), Is.True);
    }
}
