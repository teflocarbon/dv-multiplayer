using System;
using System.Linq;
using Multiplayer.Core.Shops;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Shops;

[TestFixture]
public sealed class ShopBasketTests
{
    // Module 0 costs 10, module 1 costs 25.
    private static double Price(int module) => module == 0 ? 10 : 25;

    [Test]
    public void ScansMakeOneLinePerItemAndOwner()
    {
        ShopBasket basket = new();
        basket.Add(0, 1);
        basket.Add(0, 1);
        basket.Add(0, 2);
        basket.Add(1, 2);

        Assert.That(basket.Lines, Is.EquivalentTo(new[]
        {
            new ShopBasketLine(0, 1, 2), new ShopBasketLine(0, 2, 1), new ShopBasketLine(1, 2, 1)
        }));
        Assert.That(basket.UnitsOf(0), Is.EqualTo(3));
        Assert.That(basket.Total(Price), Is.EqualTo(55));
    }

    [Test]
    public void EveryChangeBumpsTheRevision()
    {
        ShopBasket basket = new();
        uint start = basket.Revision;
        basket.Add(0, 1);
        uint scanned = basket.Revision;
        basket.Deposit(5);
        uint paid = basket.Revision;
        basket.SetDelivering(true);

        Assert.That(new[] { start, scanned, paid, basket.Revision }, Is.Ordered.Ascending.And.Unique);
    }

    [Test]
    public void NothingChangedKeepsTheRevision()
    {
        ShopBasket basket = new();
        basket.Add(0, 1);
        uint revision = basket.Revision;
        basket.Deposit(0);
        basket.SetDelivering(false);
        basket.Trim(line => line.Units);
        Assert.That(basket.TakeDeposit(), Is.EqualTo(0));

        Assert.That(basket.Revision, Is.EqualTo(revision));
    }

    [Test]
    public void OwedNeverGoesBelowZero()
    {
        ShopBasket basket = new();
        basket.Add(0, 1);
        basket.Deposit(4);
        Assert.That(basket.Owed(Price), Is.EqualTo(6));
        basket.Deposit(50);
        Assert.That(basket.Owed(Price), Is.EqualTo(0));
    }

    [Test]
    public void CheckoutTakesThePriceAndLeavesTheChange()
    {
        ShopBasket basket = new();
        basket.Add(0, 1);
        basket.Add(1, 2);
        basket.Deposit(50);

        var bought = basket.Checkout(Price);

        Assert.That(bought.Sum(l => l.Units), Is.EqualTo(2));
        Assert.That(basket.IsEmpty, Is.True);
        Assert.That(basket.Deposited, Is.EqualTo(15));
        Assert.That(basket.TakeDeposit(), Is.EqualTo(15));
        Assert.That(basket.Deposited, Is.EqualTo(0));
    }

    [Test]
    public void CheckoutRefusesTooLittleCash()
    {
        ShopBasket basket = new();
        basket.Add(1, 1);
        basket.Deposit(10);

        Assert.Throws<InvalidOperationException>(() => basket.Checkout(Price));
        Assert.That(basket.Lines, Has.Count.EqualTo(1));
    }

    [Test]
    public void TrimCutsAndDropsLines()
    {
        ShopBasket basket = new();
        basket.Add(0, 1);
        basket.Add(0, 1);
        basket.Add(0, 1);
        basket.Add(1, 2);

        int removed = basket.Trim(line => line.Module == 0 ? 1 : 0);

        Assert.That(removed, Is.EqualTo(3));
        Assert.That(basket.Lines, Is.EqualTo(new[] { new ShopBasketLine(0, 1, 1) }));
    }

    [Test]
    public void ClearingTheLinesKeepsTheCash()
    {
        ShopBasket basket = new();
        basket.Add(0, 1);
        basket.Deposit(7);
        basket.ClearLines();

        Assert.That(basket.IsEmpty, Is.True);
        Assert.That(basket.Deposited, Is.EqualTo(7));
    }
}
