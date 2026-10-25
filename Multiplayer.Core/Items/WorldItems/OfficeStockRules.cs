using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.WorldItems;

/// <summary>
/// When the server may tidy a station office's stock (docs/design/items/world-items.md, "Office
/// stock"): make a taken prop's slot again, put a knocked prop back, or clear an unowned prop a
/// player took and left. Each waits until no player could see it happen, and never touches an
/// item something with a better claim holds (lifecycle precedence: a cleanup is the lowest).
/// </summary>
public static class OfficeStockRules
{
    /// <summary>
    /// Whether a player keeps the server from tidying a place. One whose interest takes it in
    /// does. So does one still loading, wherever the place is: where they will appear isn't known
    /// yet, and before R7 a player joining beside an office watched its props vanish (F30,
    /// OI-SYNC-10).
    /// </summary>
    public static bool Protects(bool playerReadyForItems, bool placeInPlayersInterest) =>
        !playerReadyForItems || placeInPlayersInterest;

    /// <summary>
    /// Whether an unowned office prop left lying (a stock fork) may be cleared. Never one owned,
    /// claimed for an inventory slot, in Lost and Found, held or stored anywhere, simulated (a
    /// lease: it is flying or sliding) or grabbed (OI-SYNC-11).
    /// </summary>
    public static bool MayClearFork(bool owned, bool claimed, bool lostAndFound, LivePlacement placement,
        bool simulated, bool grabbed) =>
        !owned && !claimed && !lostAndFound && !simulated && !grabbed &&
        placement is LivePlacement.World or LivePlacement.StaticParent or LivePlacement.TrainInterior;

    /// <summary>
    /// Whether a knocked office prop may be put back in its place. Nobody may see where it
    /// belongs or where it lies: before R7 only its place was checked, and a prop knocked out
    /// of the office vanished from beside the player who had it (OI-SYNC-13). Nor while it is
    /// simulated or grabbed.
    /// </summary>
    public static bool MayReset(bool placeProtected, bool whereItLiesProtected, bool simulated, bool grabbed) =>
        !placeProtected && !whereItLiesProtected && !simulated && !grabbed;
}
