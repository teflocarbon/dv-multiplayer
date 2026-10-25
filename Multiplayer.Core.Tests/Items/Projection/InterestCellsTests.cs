using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.Projection;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Projection;

/// <summary>Interest is entered at 3x3 cells of 128 m and left outside 5x5 (OI-SYNC-06).</summary>
[TestFixture]
public sealed class InterestCellsTests
{
    [Test]
    public void CellsAre128MetresAndInterestIs3x3In5x5Out()
    {
        Assert.That(InterestCells.CellSize, Is.EqualTo(128f));
        Assert.That(InterestCells.EnterRadius, Is.EqualTo(1));
        Assert.That(InterestCells.KeepRadius, Is.EqualTo(2));
    }

    [TestCase(0f, 0)]
    [TestCase(127.9f, 0)]
    [TestCase(128f, 1)]
    [TestCase(-0.1f, -1)]
    [TestCase(-128f, -1)]
    [TestCase(-128.1f, -2)]
    public void ACoordinateFallsInTheCellBelowIt(float coordinate, int cell) =>
        Assert.That(InterestCells.CellOf(coordinate), Is.EqualTo(cell));

    [TestCase(0)]
    [TestCase(1)]
    public void AnItemWithinOneCellComesIn(int cellsAway) =>
        Assert.That(Wanted(cellsAway, has: false), Is.True);

    [Test]
    public void AnItemTwoCellsAwayDoesntComeIn() => Assert.That(Wanted(2, has: false), Is.False);

    [Test]
    public void AnItemThePlayerHasIsKeptTwoCellsAway() => Assert.That(Wanted(2, has: true), Is.True);

    [Test]
    public void AnItemThePlayerHasLeavesThreeCellsAway() => Assert.That(Wanted(3, has: true), Is.False);

    [Test]
    public void AnItemComesBackOnlyWithinOneCell()
    {
        // Walked out to three cells it left; back at two it stays out, and at one it comes in.
        Assert.That(Wanted(3, has: true), Is.False);
        Assert.That(Wanted(2, has: false), Is.False);
        Assert.That(Wanted(1, has: false), Is.True);
    }

    [Test]
    public void AnotherItemThePlayerHasDoesntKeepThisOne()
    {
        // The player has the near item; the one two cells away comes in only if they had it.
        HashSet<string> wanted = InterestCells.Wanted(Within(("near", 0), ("far", 2)), hasAny: true, has: item => item == "near");
        Assert.That(wanted, Is.EquivalentTo(new[] { "near" }));
    }

    private static bool Wanted(int cellsAway, bool has) =>
        InterestCells.Wanted(Within(("item", cellsAway)), has, _ => has).Contains("item");

    // Items at so many cells from the player's (the larger of the x and z distances).
    private static System.Func<int, HashSet<string>> Within(params (string Item, int CellsAway)[] items) =>
        radius => new HashSet<string>(items.Where(i => i.CellsAway <= radius).Select(i => i.Item));
}
