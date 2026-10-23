using Multiplayer.Core.Items.Inventory;
using NUnit.Framework;
using System.Collections.Generic;

namespace Multiplayer.Core.Tests.Items.Inventory;

[TestFixture]
public sealed class InventorySlotsTests
{
    [Test]
    public void AClaim_TakesTheSlotAskedFor_ElseTheLowestFreeOffTheBelt()
    {
        HashSet<int> claimed = new() { 0, 2 };

        Assert.That(InventorySlots.ChooseClaim(5, claimed, null), Is.EqualTo(5));
        Assert.That(InventorySlots.ChooseClaim(2, claimed, null), Is.EqualTo(1));
        Assert.That(InventorySlots.ChooseClaim(-1, claimed, slot => slot == 1), Is.EqualTo(3));
        Assert.That(InventorySlots.ChooseClaim(InventorySlots.FirstBeltSlot, claimed, null), Is.EqualTo(1), "never the belt");
        Assert.That(InventorySlots.ChooseClaim(-1, claimed, slot => true), Is.EqualTo(-1));
    }

    [Test]
    public void AnOrdinaryItem_TakesTheLowestFreeSlot_TheBeltLast()
    {
        Assert.That(InventorySlots.FirstFree(slot => slot < 33), Is.EqualTo(33));
        Assert.That(InventorySlots.FirstFree(slot => true), Is.EqualTo(-1));
        Assert.That(InventorySlots.IsBelt(35) && !InventorySlots.IsBelt(32) && !InventorySlots.IsValid(36), Is.True);
    }
}
