using System;
using System.Collections.Generic;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.LiveState;

namespace Multiplayer.Core.Items.ChangeCauses;

/// <summary>
/// Why an item stopped being a live item on the host, or why a client stopped showing it. Kept
/// on the host's recent removals and sent with the Destroy, so "where did my radio go?" has an
/// answer on every peer. Values are on the wire: append, never reorder.
/// </summary>
public enum ItemRemovalReason : byte
{
    Unknown,
    /// <summary>
    /// Destroyed with nothing recording why: Derail Valley's own code, or a parent object taking
    /// it down with it. The detail says what was known (its parent, where it was, the callers).
    /// </summary>
    DestroyedByGame,
    /// <summary>The host collected it into its owner's Lost and Found.</summary>
    CollectedToLostAndFound,
    /// <summary>Put into a container's cold storage (a toolbox): it lives on as a cold record.</summary>
    DepositedInContainer,
    /// <summary>Lying in a train car that was deleted, and not a player's (theirs go to Lost and Found).</summary>
    DestroyedWithCar,
    /// <summary>A banknote or coin paid into the shared wallet.</summary>
    MoneyDeposited,
    /// <summary>A job booklet, overview or report the job system destroyed (job taken, done, ended).</summary>
    JobDocumentDestroyed,
    /// <summary>An unowned office-stock fork left outside everyone's interest long enough.</summary>
    AbandonedStockForkCleared,
    /// <summary>An authored slot's old representation, replaced by the restored or replenished one.</summary>
    AuthoredSlotReplaced,
    /// <summary>Derail Valley destroyed it from an inventory (Inventory.DestroyItem).</summary>
    DestroyedFromInventory,
    /// <summary>Client only: the item left this player's interest. It still exists on the host.</summary>
    LeftInterest,
    /// <summary>A leaving holder's inventory item went cold: its state is kept as data, its home unchanged (D12).</summary>
    WentCold,
    /// <summary>
    /// Its saved state wouldn't load: the server made it again fresh from its prefab, with the same id,
    /// in its owner's Lost and Found (D9). Its entry lives on.
    /// </summary>
    RemadeFresh,
    /// <summary>Put in a bin by its owner, or by anyone when nobody owns it, and gone with the bin's destroy (D10).</summary>
    Discarded,
    /// <summary>
    /// Used up by a player (a garage key in its padlock, a spray can's paint): the server's step
    /// (UseUp). A replacement it made (the empty can) is born Replaced.
    /// </summary>
    UsedUp,
    /// <summary>Destroyed by a dev console command on the server (Debug.DestroyAllItems, Debug.DestroyBuggedObjects).</summary>
    DevConsole
}

/// <summary>
/// Why an item's placement, holder or owner changed. The host keeps the last one on the item's
/// record and sends it with the update that carries the change. Values are on the wire: append,
/// never reorder.
/// </summary>
public enum ItemMoveReason : byte
{
    Unknown,
    /// <summary>From the world (or a car or a mount) into a hand.</summary>
    PickedUp,
    /// <summary>Out of the holder's own inventory into their hand.</summary>
    TakenFromInventory,
    /// <summary>Into an inventory from a hand or the world.</summary>
    StoredInInventory,
    /// <summary>Taken straight out of another player's hand or inventory.</summary>
    TakenFromPlayer,
    Dropped,
    Thrown,
    /// <summary>Mounted on a car (an end-of-train lantern).</summary>
    Attached,
    /// <summary>Taken off its mount into the world.</summary>
    Detached,
    /// <summary>Between world, car interior and static parent with nobody holding it.</summary>
    Moved,
    /// <summary>A thrown or dropped item came to rest, committed by its physics lease.</summary>
    Settled,
    /// <summary>The owner recalled it to their reserved slot from wherever it was.</summary>
    Recalled,
    RetrievedFromLostAndFound,
    WithdrawnFromContainer,
    /// <summary>Spawned at a shop for its buyer, who owns it.</summary>
    Purchased,
    /// <summary>Given by an admin command.</summary>
    Granted,
    RestoredFromSave,
    /// <summary>In the hand (or a borrowed inventory item) of a player who disconnected.</summary>
    DroppedOnDisconnect,
    /// <summary>A job booklet summoned to the player who has the job.</summary>
    JobBookletSummoned,
    /// <summary>The host moved an essential's claim to a free slot: two of the owner's claimed one.</summary>
    ClaimSlotMoved,
    /// <summary>Fell through the terrain and was put back at its last safe pose.</summary>
    RescuedFromBelowMap,
    /// <summary>
    /// The host put the item back where its record says, against a client or its own game's
    /// copy: a refused change, a rolled-back operation, a repaired holder.
    /// </summary>
    HostCorrection,
    /// <summary>A job overview thrown back out of the office bin: its job couldn't be re-rolled.</summary>
    ThrownOutOfBin,
    /// <summary>Installed on a car as a gadget (gadgets.md).</summary>
    InstalledAsGadget,
    /// <summary>Taken off the car it was installed on, by a player or forced (a deleted car, broken glass).</summary>
    RemovedAsGadget,
    /// <summary>
    /// Made by the server for its owner, into their inventory: one of Derail Valley's essentials
    /// they had none of (essentials.md, "Starter kit and issued essentials").
    /// </summary>
    Issued,
    /// <summary>Made by the server for its owner, into their inventory: their starter kit, at their first join.</summary>
    StarterKit,
    /// <summary>The server collected it into its owner's Lost and Found.</summary>
    CollectedToLostAndFound,
    /// <summary>Put in a container's slot.</summary>
    DepositedInContainer,
    /// <summary>Moved from one of its holder's inventory slots to another (OI-SLOT-01).</summary>
    SlotMoved,
    /// <summary>A cold inventory item made again in its slot as its holder joined (D12).</summary>
    WentLive,
    /// <summary>Loaded into a tool's magazine (a cassette into a boombox).</summary>
    LoadedIntoMagazine,
    /// <summary>Out of a tool's magazine: ejected, or taken out.</summary>
    UnloadedFromMagazine,
    /// <summary>Put in a bin by someone it isn't theirs to bin: put down beside the bin (D10).</summary>
    BinRefused
}

/// <summary>Where an item is, by whom it's held, and who owns it: what a move changes.</summary>
public readonly struct ItemWhereabouts : IEquatable<ItemWhereabouts>
{
    public readonly LivePlacement Placement;
    /// <summary>The identity of the player whose hand or inventory it's in, or empty.</summary>
    public readonly Guid Holder;
    /// <summary>The owner's identity, or empty.</summary>
    public readonly Guid Owner;

    public ItemWhereabouts(LivePlacement placement, Guid holder, Guid owner)
    {
        Placement = placement;
        Holder = ItemMoveClassifier.IsHeld(placement) ? holder : Guid.Empty;
        Owner = owner;
    }

    public bool Equals(ItemWhereabouts other) => Placement == other.Placement &&
        Holder == other.Holder && Owner == other.Owner;

    public override bool Equals(object obj) => obj is ItemWhereabouts other && Equals(other);
    public override int GetHashCode() => ((int)Placement * 397) ^ Holder.GetHashCode() ^ (Owner.GetHashCode() * 7);
}

/// <summary>
/// Names the move between two whereabouts from what changed, for changes no caller explained
/// (a client's own pickup, the host's game moving its copy), and describes it for the log.
/// </summary>
public static class ItemMoveClassifier
{
    /// <summary>Whether anything a move reports changed: placement, holder or owner. A pose alone doesn't count.</summary>
    public static bool Changed(ItemWhereabouts before, ItemWhereabouts after) => !before.Equals(after);

    public static ItemMoveReason Classify(ItemWhereabouts before, ItemWhereabouts after, bool thrown)
    {
        if (after.Placement == LivePlacement.Installed)
            return before.Placement == after.Placement ? ItemMoveReason.Unknown : ItemMoveReason.InstalledAsGadget;
        if (before.Placement == LivePlacement.Installed)
            return ItemMoveReason.RemovedAsGadget;
        if (after.Placement == LivePlacement.Magazine)
            return before.Placement == after.Placement ? ItemMoveReason.Unknown : ItemMoveReason.LoadedIntoMagazine;
        if (before.Placement == LivePlacement.Magazine)
            return ItemMoveReason.UnloadedFromMagazine;
        bool fromOtherPlayer = IsHeld(before.Placement) && before.Holder != Guid.Empty &&
            IsHeld(after.Placement) && after.Holder != before.Holder;
        switch (after.Placement)
        {
            case LivePlacement.PlayerHand:
                if (fromOtherPlayer)
                    return ItemMoveReason.TakenFromPlayer;
                return before.Placement == LivePlacement.PlayerInventory
                    ? ItemMoveReason.TakenFromInventory
                    : before.Placement == LivePlacement.PlayerHand ? ItemMoveReason.Unknown : ItemMoveReason.PickedUp;
            case LivePlacement.PlayerInventory:
                if (fromOtherPlayer)
                    return ItemMoveReason.TakenFromPlayer;
                return before.Placement == LivePlacement.PlayerInventory
                    ? ItemMoveReason.Unknown : ItemMoveReason.StoredInInventory;
            case LivePlacement.World:
            case LivePlacement.TrainInterior:
            case LivePlacement.StaticParent:
                if (IsHeld(before.Placement))
                    return thrown ? ItemMoveReason.Thrown : ItemMoveReason.Dropped;
                if (before.Placement is LivePlacement.Attached or LivePlacement.SnappedAttachment)
                    return ItemMoveReason.Detached;
                return before.Placement == after.Placement ? ItemMoveReason.Unknown : ItemMoveReason.Moved;
            case LivePlacement.Attached:
            case LivePlacement.SnappedAttachment:
                return before.Placement == after.Placement ? ItemMoveReason.Unknown : ItemMoveReason.Attached;
            default:
                return ItemMoveReason.Unknown;
        }
    }

    /// <summary>"world -> Bentgear29's hand, owner none -> Bentgear29": what changed, for a cause's detail.</summary>
    /// <param name="playerName">A player's name from their identity, if known; else the identity's first 8 digits.</param>
    public static string Describe(ItemWhereabouts before, ItemWhereabouts after, Func<Guid, string> playerName = null)
    {
        string Name(Guid id) => id == Guid.Empty ? "none"
            : playerName?.Invoke(id) is { Length: > 0 } name ? name : id.ToString("N").Substring(0, 8);
        List<string> parts = new();
        if (before.Placement != after.Placement || before.Holder != after.Holder)
            parts.Add(Where(before, Name) + " -> " + Where(after, Name));
        if (before.Owner != after.Owner)
            parts.Add("owner " + Name(before.Owner) + " -> " + Name(after.Owner));
        return string.Join(", ", parts);
    }

    internal static bool IsHeld(LivePlacement placement) =>
        placement is LivePlacement.PlayerHand or LivePlacement.PlayerInventory;

    private static string Where(ItemWhereabouts at, Func<Guid, string> name) => at.Placement switch
    {
        LivePlacement.World => "world",
        LivePlacement.PlayerHand => name(at.Holder) + "'s hand",
        LivePlacement.PlayerInventory => name(at.Holder) + "'s inventory",
        LivePlacement.Container => "container",
        LivePlacement.Attached => "car mount",
        LivePlacement.LostAndFound => "Lost and Found",
        LivePlacement.Installed => "installed",
        LivePlacement.Destroyed => "destroyed",
        LivePlacement.TrainInterior => "train car",
        LivePlacement.StaticParent => "static parent",
        LivePlacement.SnappedAttachment => "snapped",
        LivePlacement.Magazine => "magazine",
        _ => at.Placement.ToString()
    };
}

/// <summary>One line per item change, the same on every peer.</summary>
public static class ItemChangeLogFormat
{
    /// <summary>"Item JobOverview[CME-SU-12] (netId 801) removed: JobDocumentDestroyed (detail)".</summary>
    public static string Removed(string itemName, ushort netId, ChangeCause<ItemRemovalReason> cause) =>
        $"Item {ItemName(itemName)} (netId {netId}) removed: {cause}";

    /// <summary>"Item ItemRadio (netId 763) moved: PickedUp by Bentgear29 (world -> Bentgear29's hand)".</summary>
    public static string Moved(string itemName, ushort netId, ChangeCause<ItemMoveReason> cause) =>
        $"Item {ItemName(itemName)} (netId {netId}) moved: {cause}";

    /// <summary>Unity's "(Clone)" suffix says nothing about the item.</summary>
    public static string ItemName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "?";
        const string clone = "(Clone)";
        return name.EndsWith(clone, StringComparison.Ordinal) ? name.Substring(0, name.Length - clone.Length).TrimEnd() : name;
    }
}

/// <summary>An item the host no longer has, and why: what's left to ask about once its record is gone.</summary>
public readonly struct ItemRemovalRecord
{
    public readonly ushort NetId;
    public readonly string ItemName;
    /// <summary>The owner's identity, or empty.</summary>
    public readonly Guid Owner;
    public readonly ItemWhereabouts LastWhereabouts;
    public readonly ChangeCause<ItemRemovalReason> Cause;
    public readonly ChangeCause<ItemMoveReason> LastMove;

    public ItemRemovalRecord(ushort netId, string itemName, ItemWhereabouts lastWhereabouts,
        ChangeCause<ItemRemovalReason> cause, ChangeCause<ItemMoveReason> lastMove)
    {
        NetId = netId;
        ItemName = itemName ?? string.Empty;
        Owner = lastWhereabouts.Owner;
        LastWhereabouts = lastWhereabouts;
        Cause = cause;
        LastMove = lastMove;
    }
}

/// <summary>
/// The host's most recent item removals, newest last. NetIds are reused, so a lookup returns the
/// newest removal with that id.
/// </summary>
public sealed class RecentItemRemovals
{
    private readonly ItemRemovalRecord[] entries;
    private int next;
    private int count;

    public RecentItemRemovals(int capacity = 256)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        entries = new ItemRemovalRecord[capacity];
    }

    public int Count => count;

    public void Add(ItemRemovalRecord record)
    {
        entries[next] = record;
        next = (next + 1) % entries.Length;
        if (count < entries.Length)
            count++;
    }

    public bool TryGetLatest(ushort netId, out ItemRemovalRecord record)
    {
        for (int i = 1; i <= count; i++)
        {
            ItemRemovalRecord candidate = entries[(next - i + entries.Length) % entries.Length];
            if (candidate.NetId == netId)
            {
                record = candidate;
                return true;
            }
        }
        record = default;
        return false;
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<ItemRemovalRecord> Snapshot()
    {
        var result = new List<ItemRemovalRecord>(count);
        for (int i = count; i >= 1; i--)
            result.Add(entries[(next - i + entries.Length) % entries.Length]);
        return result;
    }

    public void Clear()
    {
        Array.Clear(entries, 0, entries.Length);
        next = 0;
        count = 0;
    }
}
