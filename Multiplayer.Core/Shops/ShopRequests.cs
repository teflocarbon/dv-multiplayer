namespace Multiplayer.Core.Shops;

/// <summary>What a player asked a shop register to do.</summary>
public enum ShopRequestKind : byte
{
    Scan,
    Deposit,
    Buy,
    Cancel,
}

/// <summary>Why the server refused a shop request. On the wire: append, never reorder.</summary>
public enum ShopRefusal : byte
{
    None,
    NotNear,
    NoScanner,
    NotSoldHere,
    NotAvailable,
    NeedsLicence,
    GarageUnlocked,
    OutOfStock,
    NothingOwed,
    WalletEmpty,
    MoneyNotYours,
    NotEnoughCash,
    BasketEmpty,
    BasketChanged,
    Busy,
}

/// <summary>What last happened at a register, for every game to show and play. On the wire: append, never reorder.</summary>
public enum ShopRegisterEvent : byte
{
    None,
    Scanned,
    ScanRefused,
    CashAdded,
    Bought,
    NotEnoughCash,
    Cancelled,
    CashReturned,
    Delivered,
    LinesDropped,
}

public static class ShopRefusals
{
    /// <summary>The refusal as the player reads it on their HUD.</summary>
    public static string Describe(ShopRefusal refusal) => refusal switch
    {
        ShopRefusal.NotNear => "You're too far away.",
        ShopRefusal.NoScanner => "Hold the shop's scanner to add items.",
        ShopRefusal.NotSoldHere => "This shop doesn't sell that.",
        ShopRefusal.NotAvailable => "Not available in this game mode.",
        ShopRefusal.NeedsLicence => "You need a licence to buy this.",
        ShopRefusal.GarageUnlocked => "That garage is already unlocked.",
        ShopRefusal.OutOfStock => "Out of stock.",
        ShopRefusal.NothingOwed => "Nothing to pay for.",
        ShopRefusal.WalletEmpty => "The wallet is empty.",
        ShopRefusal.MoneyNotYours => "That money isn't yours to spend.",
        ShopRefusal.NotEnoughCash => "Not enough cash paid in.",
        ShopRefusal.BasketEmpty => "The basket is empty.",
        ShopRefusal.BasketChanged => "Someone changed the basket. Check it and press Buy again.",
        ShopRefusal.Busy => "Wait for the last purchase to come out.",
        _ => string.Empty,
    };
}
