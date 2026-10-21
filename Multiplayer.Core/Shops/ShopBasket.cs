using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Shops;

/// <summary>One line of a shop register's basket: units of one shelf item, owned by whoever scanned them.</summary>
public readonly struct ShopBasketLine : IEquatable<ShopBasketLine>
{
    public ShopBasketLine(int module, byte owner, int units)
    {
        Module = module;
        Owner = owner;
        Units = units;
    }

    /// <summary>The item's index in its shop (<c>Shop.scanItemResourceModules</c>).</summary>
    public int Module { get; }

    /// <summary>The player who scanned these units, who owns what they buy.</summary>
    public byte Owner { get; }

    public int Units { get; }

    public bool Equals(ShopBasketLine other) => Module == other.Module && Owner == other.Owner && Units == other.Units;
    public override bool Equals(object obj) => obj is ShopBasketLine other && Equals(other);
    public override int GetHashCode() => (Module * 397) ^ (Owner * 31) ^ Units;
    public override string ToString() => $"module {Module} x{Units} for P{Owner}";
}

/// <summary>
/// A shop register as the server keeps it: one basket that everyone at the shop shares, the cash
/// paid in, and whether a purchase is still being delivered. Every change bumps
/// <see cref="Revision"/>, so a game applies only newer states, and remembers who made it, so a Buy
/// can be refused when someone else changed the basket its presser saw. See docs/design/shops.md.
/// </summary>
public sealed class ShopBasket
{
    // Enough of who changed what for a Buy pressed a few round trips after the basket it names.
    private const int ChangesKept = 64;

    private readonly List<ShopBasketLine> lines = new();
    private readonly Queue<(uint Revision, byte Actor)> changes = new();

    public IReadOnlyList<ShopBasketLine> Lines => lines;
    public double Deposited { get; private set; }
    public bool Delivering { get; private set; }
    public uint Revision { get; private set; }

    public bool IsEmpty => lines.Count == 0;

    /// <summary>Units of <paramref name="module"/> in the basket, everyone's.</summary>
    public int UnitsOf(int module) => lines.Where(l => l.Module == module).Sum(l => l.Units);

    /// <summary>The basket's price: each line's units at its module's unit price.</summary>
    public double Total(Func<int, double> unitPrice) => lines.Sum(l => l.Units * unitPrice(l.Module));

    /// <summary>What is still owed: the price less what has been paid in, never below zero.</summary>
    public double Owed(Func<int, double> unitPrice) => Math.Max(0, Total(unitPrice) - Deposited);

    /// <summary>
    /// Whether anyone but <paramref name="player"/> changed the basket since <paramref name="revision"/>
    /// (the server itself counts as someone else). A player's own changes in between are theirs to
    /// have seen: they made them, whether or not their game had heard back yet.
    /// </summary>
    public bool ChangedByOthersSince(uint revision, byte player)
    {
        if (revision == Revision)
            return false;
        if (revision > Revision)
            return true;
        uint oldestKept = changes.Count == 0 ? Revision : changes.Peek().Revision;
        if (revision + 1 < oldestKept)
            return true;
        return changes.Any(c => c.Revision > revision && c.Actor != player);
    }

    /// <summary>Adds one unit of <paramref name="module"/> to <paramref name="owner"/>'s line.</summary>
    public void Add(int module, byte owner)
    {
        int index = lines.FindIndex(l => l.Module == module && l.Owner == owner);
        if (index < 0)
            lines.Add(new ShopBasketLine(module, owner, 1));
        else
            lines[index] = new ShopBasketLine(module, owner, lines[index].Units + 1);
        Changed(owner);
    }

    public void Deposit(double amount, byte actor = 0)
    {
        if (amount <= 0)
            return;
        Deposited += amount;
        Changed(actor);
    }

    /// <summary>Takes all the cash out (handed back, or as change), leaving the basket.</summary>
    public double TakeDeposit(byte actor = 0)
    {
        double taken = Deposited;
        if (taken > 0)
        {
            Deposited = 0;
            Changed(actor);
        }
        return taken;
    }

    /// <summary>
    /// Pays for the basket: takes its total out of the deposit and empties it. Returns the lines
    /// bought; the change stays in <see cref="Deposited"/> for the caller to hand back.
    /// </summary>
    public IReadOnlyList<ShopBasketLine> Checkout(Func<int, double> unitPrice, byte actor = 0)
    {
        double total = Total(unitPrice);
        if (total > Deposited)
            throw new InvalidOperationException($"the basket costs {total}, {Deposited} is paid in");

        ShopBasketLine[] bought = lines.ToArray();
        Deposited -= total;
        lines.Clear();
        Changed(actor);
        return bought;
    }

    /// <summary>Empties the basket (Cancel), leaving the cash.</summary>
    public void ClearLines(byte actor = 0)
    {
        if (lines.Count == 0)
            return;
        lines.Clear();
        Changed(actor);
    }

    /// <summary>Cuts or drops lines: <paramref name="unitsAllowed"/> says how many of a line's units may stay.</summary>
    public int Trim(Func<ShopBasketLine, int> unitsAllowed, byte actor = 0)
    {
        int removed = 0;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            int allowed = Math.Max(0, Math.Min(lines[i].Units, unitsAllowed(lines[i])));
            if (allowed == lines[i].Units)
                continue;
            removed += lines[i].Units - allowed;
            if (allowed == 0)
                lines.RemoveAt(i);
            else
                lines[i] = new ShopBasketLine(lines[i].Module, lines[i].Owner, allowed);
        }
        if (removed > 0)
            Changed(actor);
        return removed;
    }

    public void SetDelivering(bool delivering, byte actor = 0)
    {
        if (Delivering == delivering)
            return;
        Delivering = delivering;
        Changed(actor);
    }

    private void Changed(byte actor)
    {
        Revision++;
        changes.Enqueue((Revision, actor));
        while (changes.Count > ChangesKept)
            changes.Dequeue();
    }
}
