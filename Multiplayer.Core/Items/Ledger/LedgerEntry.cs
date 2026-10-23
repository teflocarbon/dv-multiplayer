using System;
using Multiplayer.Core.Items.ChangeCauses;

namespace Multiplayer.Core.Items.Ledger;

/// <summary>Which kind of place an owned item is in. Saved by name; see docs/design/owned-items.md.</summary>
public enum HomeKind : byte
{
    /// <summary>In a player's hand, inventory or belt.</summary>
    With,
    /// <summary>Loose in the world, in a train interior or on a static parent (a shelf).</summary>
    World,
    /// <summary>Attached to a car, as an EOT lantern is.</summary>
    Attached,
    /// <summary>Snapped to a snap point (a hanger, a mount).</summary>
    Snapped,
    /// <summary>Stored in a cold container.</summary>
    Container,
    LostAndFound,
    /// <summary>Installed on a car as a gadget.</summary>
    Installed,
    /// <summary>The store its last place named didn't hold it at load. Kept, and looked for again.</summary>
    Missing
}

/// <summary>How a player holds an item that is <see cref="HomeKind.With"/> them.</summary>
public enum HoldKind : byte
{
    None,
    Hand,
    Inventory,
    Belt
}

/// <summary>How an owned item came to be owned. Saved by name.</summary>
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
    /// <summary>Found in a store of a save written before the ledger.</summary>
    Migrated,
    /// <summary>
    /// A second live item carrying an id the ledger already had: the copy got a new id, and
    /// <see cref="Birth.From"/> is the one it copied.
    /// </summary>
    Duplicate
}

/// <summary>
/// Where an owned item is: the kind of place and what names it there. It says which store must hold
/// the item, not its pose; the pose stays with the store.
/// </summary>
public readonly struct Home : IEquatable<Home>
{
    private Home(HomeKind place, Guid holder, HoldKind hold, int slot, string anchor,
        int gadgetUid)
    {
        Place = place;
        Holder = holder;
        Hold = hold;
        Slot = slot;
        Anchor = anchor ?? string.Empty;
        GadgetUid = gadgetUid;
    }

    public HomeKind Place { get; }
    /// <summary>The identity of the player who has it (<see cref="HomeKind.With"/> only).</summary>
    public Guid Holder { get; }
    public HoldKind Hold { get; }
    /// <summary>The inventory slot, or -1.</summary>
    public int Slot { get; }
    /// <summary>
    /// What it is in or on: a car's GUID (train interior, attached, installed), a static parent's key,
    /// a snap parent, or a cold container's id. Empty for loose in the world, Lost and Found and with a
    /// player.
    /// </summary>
    public string Anchor { get; }
    /// <summary>The gadget's customizer UID (<see cref="HomeKind.Installed"/> only).</summary>
    public int GadgetUid { get; }

    public bool IsWithPlayer => Place == HomeKind.With;

    public static Home With(Guid holder, HoldKind hold, int slot = -1) =>
        new(HomeKind.With, holder, hold, slot, null, 0);
    public static Home World(string parent = null) => new(HomeKind.World, Guid.Empty, HoldKind.None, -1, parent, 0);
    public static Home Attached(string car) => new(HomeKind.Attached, Guid.Empty, HoldKind.None, -1, car, 0);
    public static Home Snapped(string parent) => new(HomeKind.Snapped, Guid.Empty, HoldKind.None, -1, parent, 0);
    public static Home Container(Guid containerId) =>
        new(HomeKind.Container, Guid.Empty, HoldKind.None, -1, containerId.ToString("D"), 0);
    public static Home LostAndFound => new(HomeKind.LostAndFound, Guid.Empty, HoldKind.None, -1, null, 0);
    public static Home Installed(string car, int gadgetUid) =>
        new(HomeKind.Installed, Guid.Empty, HoldKind.None, -1, car, gadgetUid);
    public static Home Missing => new(HomeKind.Missing, Guid.Empty, HoldKind.None, -1, null, 0);

    /// <summary>Rebuilds a saved place. Fields a place doesn't use are dropped.</summary>
    public static Home From(HomeKind place, Guid holder, HoldKind hold, int slot, string anchor,
        int gadgetUid) => place switch
    {
        HomeKind.With => With(holder, hold, slot),
        HomeKind.Installed => Installed(anchor, gadgetUid),
        HomeKind.World or HomeKind.Attached or HomeKind.Snapped or HomeKind.Container =>
            new(place, Guid.Empty, HoldKind.None, -1, anchor, 0),
        _ => new(place, Guid.Empty, HoldKind.None, -1, null, 0)
    };

    public bool Equals(Home other) => Place == other.Place && Holder == other.Holder &&
        Hold == other.Hold && Slot == other.Slot && GadgetUid == other.GadgetUid &&
        string.Equals(Anchor, other.Anchor, StringComparison.Ordinal);
    public override bool Equals(object obj) => obj is Home other && Equals(other);
    public override int GetHashCode() => ((int)Place * 397) ^ Holder.GetHashCode() ^ (Anchor?.GetHashCode() ?? 0) ^ Slot;
    public static bool operator ==(Home a, Home b) => a.Equals(b);
    public static bool operator !=(Home a, Home b) => !a.Equals(b);

    public override string ToString() => Place switch
    {
        HomeKind.With => $"with {Holder:D} ({Hold}{(Slot >= 0 ? ", slot " + Slot : "")})",
        HomeKind.Installed => $"installed on {Anchor} (UID {GadgetUid})",
        _ => Anchor.Length == 0 ? Place.ToString() : $"{Place} {Anchor}"
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
    public LedgerEntry(Guid id, Guid owner, string prefab, Home where, Birth born,
        int claimSlot = -1, bool shopStocked = false, End ended = null)
    {
        Id = id;
        Owner = owner;
        Prefab = prefab ?? string.Empty;
        Where = where;
        Born = born;
        ClaimSlot = claimSlot;
        ShopStocked = shopStocked;
        Ended = ended;
    }

    public Guid Id { get; }
    /// <summary>The owner's identity: a player's ID now, a company's later.</summary>
    public Guid Owner { get; internal set; }
    public string Prefab { get; }
    public Home Where { get; internal set; }
    /// <summary>An essential's retrieval claim slot in its owner's inventory, or -1.</summary>
    public int ClaimSlot { get; internal set; }
    /// <summary>The item still has vanilla's Restock flag, so it counts against its owner's shop stock.</summary>
    public bool ShopStocked { get; internal set; }
    public Birth Born { get; internal set; }
    public End Ended { get; internal set; }

    public bool IsLive => Ended == null;

    public LedgerEntry Clone() => new(Id, Owner, Prefab, Where, Born, ClaimSlot, ShopStocked, Ended);

    public override string ToString() =>
        $"{Prefab} {Id:D} of {Owner:D}: {(IsLive ? Where.ToString() : "ended " + Ended.Reason)}";
}
