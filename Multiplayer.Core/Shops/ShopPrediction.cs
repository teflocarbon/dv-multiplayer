using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Shops;

/// <summary>One of this player's shop requests the server hasn't answered yet.</summary>
public readonly struct PendingShopRequest
{
    public PendingShopRequest(uint id, ShopRequestKind kind, int module = -1, double amount = 0, float sentAt = 0)
    {
        Id = id;
        Kind = kind;
        Module = module;
        Amount = amount;
        SentAt = sentAt;
    }

    public uint Id { get; }
    public ShopRequestKind Kind { get; }
    /// <summary>A scan's shelf item.</summary>
    public int Module { get; }
    /// <summary>A deposit's cash.</summary>
    public double Amount { get; }
    public float SentAt { get; }
}

/// <summary>A register as this player's game shows it: the server's state with their own requests applied.</summary>
public sealed class PredictedRegister
{
    public List<ShopBasketLine> Lines { get; } = new();
    public double Deposited { get; set; }
    public bool Delivering { get; set; }

    public int UnitsOf(int module) => Lines.Where(l => l.Module == module).Sum(l => l.Units);
    public double Total(Func<int, double> unitPrice) => Lines.Sum(l => l.Units * unitPrice(l.Module));
    public double Owed(Func<int, double> unitPrice) => Math.Max(0, Total(unitPrice) - Deposited);
}

/// <summary>
/// Optimistic shop requests (docs/design/shops.md, "Optimistic interaction"). A player at 250 ms
/// sees their own scan, payment, Buy and Cancel at once: their game shows the server's last state
/// with their pending requests applied, in the order they were made. The server's state acknowledges
/// each request it applied; a refusal names it. Either way the request leaves the overlay, so what
/// the game shows is the server's state again, at most a round trip after it was predicted.
/// </summary>
public sealed class ShopPrediction
{
    // A request with no answer in this long is dropped: the server's state is what the game shows then.
    public const float RequestTimeoutSeconds = 5f;

    private readonly List<PendingShopRequest> pending = new();

    public IReadOnlyList<PendingShopRequest> Pending => pending;

    public void Add(PendingShopRequest request) => pending.Add(request);

    /// <summary>The server applied every request up to <paramref name="requestId"/> (requests reach it in order).</summary>
    public bool Acknowledge(uint requestId) => pending.RemoveAll(r => r.Id <= requestId) > 0;

    /// <summary>The server refused <paramref name="requestId"/>.</summary>
    public bool Refuse(uint requestId) => pending.RemoveAll(r => r.Id == requestId) > 0;

    /// <summary>Drops requests older than the timeout.</summary>
    public bool Expire(float now) => pending.RemoveAll(r => now - r.SentAt > RequestTimeoutSeconds) > 0;

    public void Clear() => pending.Clear();

    /// <summary>The register as this player sees it: <paramref name="lines"/>, <paramref name="deposited"/>
    /// and <paramref name="delivering"/> from the server, then their pending requests.</summary>
    public PredictedRegister Predict(IEnumerable<ShopBasketLine> lines, double deposited, bool delivering, byte player,
        Func<int, double> unitPrice)
    {
        PredictedRegister register = new() { Deposited = deposited, Delivering = delivering };
        register.Lines.AddRange(lines ?? Enumerable.Empty<ShopBasketLine>());
        foreach (PendingShopRequest request in pending)
        {
            switch (request.Kind)
            {
                case ShopRequestKind.Scan when !register.Delivering:
                    int index = register.Lines.FindIndex(l => l.Module == request.Module && l.Owner == player);
                    if (index < 0)
                        register.Lines.Add(new ShopBasketLine(request.Module, player, 1));
                    else
                        register.Lines[index] = new ShopBasketLine(request.Module, player, register.Lines[index].Units + 1);
                    break;
                case ShopRequestKind.Deposit:
                    register.Deposited += request.Amount;
                    break;
                case ShopRequestKind.Buy when !register.Delivering && register.Lines.Count > 0:
                    double total = register.Total(unitPrice);
                    if (total > register.Deposited)
                        break;
                    register.Lines.Clear();
                    // The change comes out as a banknote at once.
                    register.Deposited = 0;
                    register.Delivering = true;
                    break;
                case ShopRequestKind.Cancel when !register.Delivering:
                    register.Lines.Clear();
                    register.Deposited = 0;
                    break;
            }
        }
        return register;
    }
}
