using System;
using Multiplayer.Core.Items.ChangeCauses;

namespace Multiplayer.Core.Items.Ledger;

/// <summary>
/// The kind of home an owned item has (docs/design/items/ledger.md, "Homes"). Saved by name, so
/// the order means nothing and values may be appended.
/// </summary>
public enum HomeKind : byte
{
    /// <summary>An exact inventory slot of a player, by identity (the VR belt is slots 33-35).</summary>
    Slot,
    /// <summary>A player's hand, by identity. Never saved: a save writes a hand item to a free slot.</summary>
    Hand,
    /// <summary>Loose in the world, with its pose (live state keeps the pose).</summary>
    World,
    /// <summary>Loose in a car, by the car's GUID.</summary>
    TrainInterior,
    /// <summary>On a static parent (a station shelf), by its key.</summary>
    Shelf,
    /// <summary>In a cold container's slot, by the container's id.</summary>
    Container,
    /// <summary>Its owner's Lost and Found.</summary>
    LostAndFound,
    /// <summary>A gadget installed on a car (by its GUID) or on the world (<see cref="Home.WorldAnchor"/>). The gadget keeps its own UID (D13).</summary>
    Installed,
    /// <summary>An end-of-train lantern on a car's end, by the car's GUID.</summary>
    Attached,
    /// <summary>Snapped to a snap point (a hanger, a mount), by its parent's key.</summary>
    Snapped,
    /// <summary>
    /// Its saved row can't be made because its prefab is gone (an item mod removed, D9). It has no
    /// object; the row is kept verbatim, written back unchanged and tried again at each load.
    /// </summary>
    Unloadable,
    /// <summary>
    /// In a live tool's magazine slot (a cassette in a boombox), by the tool's id. A tool that is a
    /// row has its ammo as rows of its container instead (<see cref="Container"/>).
    /// </summary>
    Magazine
}

/// <summary>How an owned item became owned. Saved by name.</summary>
public enum BirthReason : byte
{
    Purchased,
    StarterKit,
    /// <summary>An essential or licence paper the server gave because the player had none.</summary>
    Issued,
    /// <summary>An admin's /give.</summary>
    Granted,
    /// <summary>An unowned personal item that a player was the first to take.</summary>
    Claimed,
    /// <summary>Made by vanilla as it used up the item <see cref="Birth.From"/> (the empty tape roll).</summary>
    Replaced,
    /// <summary>Made by a player with a tool (a label from the label maker).</summary>
    Made,
    /// <summary>
    /// A second live item carrying an id the ledger already had: the copy got a new id, and
    /// <see cref="Birth.From"/> is the one it copied.
    /// </summary>
    Duplicate
}

/// <summary>
/// Where an owned item is, exactly (docs/design/items/ledger.md, "Homes"): which kind of home and
/// what names it there. Every inventory item names its slot. The pose stays with live state.
/// </summary>
public readonly struct Home : IEquatable<Home>
{
    /// <summary>The anchor of a gadget installed on the world (vanilla's <c>WorldCustomization</c>).</summary>
    public const string WorldAnchor = ":global:";

    private Home(HomeKind kind, Guid holder = default, int slot = -1, byte hand = 0, string anchor = null, bool front = false)
    {
        Kind = kind;
        Holder = holder;
        Slot = slot;
        Hand = hand;
        Anchor = anchor ?? string.Empty;
        Front = front;
    }

    public HomeKind Kind { get; }
    /// <summary>The identity of the player whose slot or hand it is (<see cref="HomeKind.Slot"/>, <see cref="HomeKind.Hand"/>).</summary>
    public Guid Holder { get; }
    /// <summary>The inventory slot, or the container slot; -1 for any other home.</summary>
    public int Slot { get; }
    /// <summary>The hand, as the wire's <c>PlayerHand</c> numbers it (0 none or desktop, 1 left, 2 right).</summary>
    public byte Hand { get; }
    /// <summary>
    /// What it is in or on: a car's GUID (train interior, attached, installed), <see cref="WorldAnchor"/>
    /// for a gadget on the world, a shelf's key, a snap parent's key, or a container's id.
    /// </summary>
    public string Anchor { get; }
    /// <summary>The car's front end (<see cref="HomeKind.Attached"/> only).</summary>
    public bool Front { get; }

    /// <summary>In a player's slot or hand.</summary>
    public bool IsWithPlayer => Kind is HomeKind.Slot or HomeKind.Hand;
    /// <summary>Loose in the world, in a car or on a shelf: where live state keeps a pose.</summary>
    public bool IsLoose => Kind is HomeKind.World or HomeKind.TrainInterior or HomeKind.Shelf;

    public static Home InSlot(Guid holder, int slot) => new(HomeKind.Slot, holder, slot);
    public static Home InHand(Guid holder, byte hand = 0) => new(HomeKind.Hand, holder, hand: hand);
    public static Home World => new(HomeKind.World);
    public static Home TrainInterior(string car) => new(HomeKind.TrainInterior, anchor: car);
    public static Home Shelf(string key) => new(HomeKind.Shelf, anchor: key);
    public static Home Container(Guid containerId, int slot = -1) =>
        new(HomeKind.Container, slot: slot, anchor: containerId.ToString("D"));
    public static Home LostAndFound => new(HomeKind.LostAndFound);
    public static Home Installed(string anchor) =>
        new(HomeKind.Installed, anchor: string.IsNullOrEmpty(anchor) ? WorldAnchor : anchor);
    public static Home Attached(string car, bool front) => new(HomeKind.Attached, anchor: car, front: front);
    public static Home Snapped(string parent) => new(HomeKind.Snapped, anchor: parent);
    public static Home Unloadable => new(HomeKind.Unloadable);
    public static Home Magazine(Guid toolId, int slot) =>
        new(HomeKind.Magazine, slot: slot, anchor: toolId.ToString("D"));

    /// <summary>Rebuilds a saved home. Fields a kind doesn't use are dropped.</summary>
    public static Home From(HomeKind kind, Guid holder, int slot, byte hand, string anchor, bool front) => kind switch
    {
        HomeKind.Slot => InSlot(holder, slot),
        HomeKind.Hand => InHand(holder, hand),
        HomeKind.TrainInterior => TrainInterior(anchor),
        HomeKind.Shelf => Shelf(anchor),
        HomeKind.Container => Guid.TryParse(anchor, out Guid container) ? Container(container, slot) : Unloadable,
        HomeKind.Installed => Installed(anchor),
        HomeKind.Attached => Attached(anchor, front),
        HomeKind.Snapped => Snapped(anchor),
        HomeKind.Magazine => Guid.TryParse(anchor, out Guid tool) ? Magazine(tool, slot) : Unloadable,
        _ => new Home(kind)
    };

    public bool Equals(Home other) => Kind == other.Kind && Holder == other.Holder && Slot == other.Slot &&
        Hand == other.Hand && Front == other.Front &&
        string.Equals(Anchor, other.Anchor, StringComparison.Ordinal);
    public override bool Equals(object obj) => obj is Home other && Equals(other);
    public override int GetHashCode() =>
        ((int)Kind * 397) ^ Holder.GetHashCode() ^ (Anchor?.GetHashCode() ?? 0) ^ (Slot * 31);
    public static bool operator ==(Home a, Home b) => a.Equals(b);
    public static bool operator !=(Home a, Home b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        HomeKind.Slot => $"slot {Slot} of {Holder:D}",
        HomeKind.Hand => $"hand of {Holder:D}",
        HomeKind.Container => $"container {Anchor} slot {Slot}",
        HomeKind.Magazine => $"magazine of {Anchor} slot {Slot}",
        HomeKind.Installed => $"installed on {Anchor}",
        HomeKind.Attached => $"attached to {Anchor} ({(Front ? "front" : "rear")})",
        _ => Anchor.Length == 0 ? Kind.ToString() : $"{Kind} {Anchor}"
    };
}

/// <summary>How an entry began.</summary>
public sealed class Birth
{
    public Birth(BirthReason reason, long utcTicks, Guid by, Guid from = default, string detail = null)
    {
        Reason = reason;
        UtcTicks = utcTicks;
        By = by;
        From = from;
        Detail = detail ?? string.Empty;
    }

    public BirthReason Reason { get; }
    public long UtcTicks { get; }
    /// <summary>The identity of the player who caused it, or empty for the server.</summary>
    public Guid By { get; }
    /// <summary>The item this one replaced or copied, or empty.</summary>
    public Guid From { get; }
    public string Detail { get; }
}

/// <summary>How an entry's life ended.</summary>
public sealed class End
{
    public End(ItemRemovalReason reason, long utcTicks, Guid replacedBy = default, string detail = null)
    {
        Reason = reason;
        UtcTicks = utcTicks;
        ReplacedBy = replacedBy;
        Detail = detail ?? string.Empty;
    }

    public ItemRemovalReason Reason { get; }
    public long UtcTicks { get; }
    /// <summary>The item vanilla made in its place, or empty.</summary>
    public Guid ReplacedBy { get; }
    public string Detail { get; }
}

/// <summary>
/// One owned item's life in the server's ledger. Only <see cref="ItemLedger"/> changes it;
/// everything else reads it.
/// </summary>
public sealed class LedgerEntry
{
    public LedgerEntry(Guid id, OwnerRef owner, string prefab, Home home, Birth born,
        int claimSlot = -1, bool shopStocked = false, End ended = null, bool essential = false)
    {
        Id = id;
        Owner = owner;
        Prefab = prefab ?? string.Empty;
        Home = home;
        Born = born;
        ClaimSlot = claimSlot;
        ShopStocked = shopStocked;
        Ended = ended;
        Essential = essential || claimSlot >= 0;
    }

    public Guid Id { get; }
    public OwnerRef Owner { get; internal set; }
    public string Prefab { get; }
    /// <summary>Where it is, exactly. Only a ledger change moves it.</summary>
    public Home Home { get; internal set; }
    /// <summary>
    /// An essential's claim: the slot of its owner's inventory it comes back to, or -1. Its home is
    /// wherever it is; the ledger assigns the claim, one per slot per owner.
    /// </summary>
    public int ClaimSlot { get; internal set; }
    /// <summary>An essential (a radio, a map): it gets a claim slot when it is issued or first stashed.</summary>
    public bool Essential { get; }
    /// <summary>The item still has vanilla's Restock flag, so it counts against its owner's shop stock.</summary>
    public bool ShopStocked { get; internal set; }
    public Birth Born { get; internal set; }
    public End Ended { get; internal set; }

    public bool IsLive => Ended == null;

    public LedgerEntry Clone() => new(Id, Owner, Prefab, Home, Born, ClaimSlot, ShopStocked, Ended, Essential);

    public override string ToString() =>
        $"{Prefab} {Id:D} of {Owner}: {(IsLive ? Home + (ClaimSlot >= 0 ? $", claims slot {ClaimSlot}" : "") : "ended " + Ended.Reason)}";
}
