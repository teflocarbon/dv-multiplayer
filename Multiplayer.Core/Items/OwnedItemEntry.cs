using System;

namespace Multiplayer.Core.Items;

/// <summary>Which kind of place an owned item is in. Saved by name; see docs/design/owned-items.md.</summary>
public enum OwnedItemPlace : byte
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

/// <summary>How a player holds an item that is <see cref="OwnedItemPlace.With"/> them.</summary>
public enum OwnedItemHold : byte
{
    None,
    Hand,
    Inventory,
    Belt
}

/// <summary>How an owned item came to be owned. Saved by name.</summary>
public enum OwnedItemBirth : byte
{
    Purchased,
    StarterKit,
    /// <summary>An essential or licence paper the server gave because the player had none.</summary>
    Issued,
    /// <summary>An admin's /give.</summary>
    Granted,
    /// <summary>An unowned personal item that a player was the first to take.</summary>
    Claimed,
    /// <summary>Made by vanilla as it used up the item <see cref="OwnedItemBorn.From"/> (the empty tape roll).</summary>
    Replaced,
    /// <summary>Made by a player with a tool (a label from the label maker).</summary>
    Made,
    /// <summary>Found in a store of a save written before the ledger.</summary>
    Migrated,
    /// <summary>
    /// A second live item carrying an id the ledger already had: the copy got a new id, and
    /// <see cref="OwnedItemBorn.From"/> is the one it copied.
    /// </summary>
    Duplicate
}

/// <summary>
/// Where an owned item is: the kind of place and what names it there. It says which store must hold
/// the item, not its pose; the pose stays with the store.
/// </summary>
public readonly struct OwnedItemWhere : IEquatable<OwnedItemWhere>
{
    private OwnedItemWhere(OwnedItemPlace place, Guid holder, OwnedItemHold hold, int slot, string anchor,
        int gadgetUid)
    {
        Place = place;
        Holder = holder;
        Hold = hold;
        Slot = slot;
        Anchor = anchor ?? string.Empty;
        GadgetUid = gadgetUid;
    }

    public OwnedItemPlace Place { get; }
    /// <summary>The identity of the player who has it (<see cref="OwnedItemPlace.With"/> only).</summary>
    public Guid Holder { get; }
    public OwnedItemHold Hold { get; }
    /// <summary>The inventory slot, or -1.</summary>
    public int Slot { get; }
    /// <summary>
    /// What it is in or on: a car's GUID (train interior, attached, installed), a static parent's key,
    /// a snap parent, or a cold container's id. Empty for loose in the world, Lost and Found and with a
    /// player.
    /// </summary>
    public string Anchor { get; }
    /// <summary>The gadget's customizer UID (<see cref="OwnedItemPlace.Installed"/> only).</summary>
    public int GadgetUid { get; }

    public bool IsWithPlayer => Place == OwnedItemPlace.With;

    public static OwnedItemWhere With(Guid holder, OwnedItemHold hold, int slot = -1) =>
        new(OwnedItemPlace.With, holder, hold, slot, null, 0);
    public static OwnedItemWhere World(string parent = null) => new(OwnedItemPlace.World, Guid.Empty, OwnedItemHold.None, -1, parent, 0);
    public static OwnedItemWhere Attached(string car) => new(OwnedItemPlace.Attached, Guid.Empty, OwnedItemHold.None, -1, car, 0);
    public static OwnedItemWhere Snapped(string parent) => new(OwnedItemPlace.Snapped, Guid.Empty, OwnedItemHold.None, -1, parent, 0);
    public static OwnedItemWhere Container(Guid containerId) =>
        new(OwnedItemPlace.Container, Guid.Empty, OwnedItemHold.None, -1, containerId.ToString("D"), 0);
    public static OwnedItemWhere LostAndFound => new(OwnedItemPlace.LostAndFound, Guid.Empty, OwnedItemHold.None, -1, null, 0);
    public static OwnedItemWhere Installed(string car, int gadgetUid) =>
        new(OwnedItemPlace.Installed, Guid.Empty, OwnedItemHold.None, -1, car, gadgetUid);
    public static OwnedItemWhere Missing => new(OwnedItemPlace.Missing, Guid.Empty, OwnedItemHold.None, -1, null, 0);

    /// <summary>Rebuilds a saved place. Fields a place doesn't use are dropped.</summary>
    public static OwnedItemWhere From(OwnedItemPlace place, Guid holder, OwnedItemHold hold, int slot, string anchor,
        int gadgetUid) => place switch
    {
        OwnedItemPlace.With => With(holder, hold, slot),
        OwnedItemPlace.Installed => Installed(anchor, gadgetUid),
        OwnedItemPlace.World or OwnedItemPlace.Attached or OwnedItemPlace.Snapped or OwnedItemPlace.Container =>
            new(place, Guid.Empty, OwnedItemHold.None, -1, anchor, 0),
        _ => new(place, Guid.Empty, OwnedItemHold.None, -1, null, 0)
    };

    public bool Equals(OwnedItemWhere other) => Place == other.Place && Holder == other.Holder &&
        Hold == other.Hold && Slot == other.Slot && GadgetUid == other.GadgetUid &&
        string.Equals(Anchor, other.Anchor, StringComparison.Ordinal);
    public override bool Equals(object obj) => obj is OwnedItemWhere other && Equals(other);
    public override int GetHashCode() => ((int)Place * 397) ^ Holder.GetHashCode() ^ (Anchor?.GetHashCode() ?? 0) ^ Slot;
    public static bool operator ==(OwnedItemWhere a, OwnedItemWhere b) => a.Equals(b);
    public static bool operator !=(OwnedItemWhere a, OwnedItemWhere b) => !a.Equals(b);

    public override string ToString() => Place switch
    {
        OwnedItemPlace.With => $"with {Holder:D} ({Hold}{(Slot >= 0 ? ", slot " + Slot : "")})",
        OwnedItemPlace.Installed => $"installed on {Anchor} (UID {GadgetUid})",
        _ => Anchor.Length == 0 ? Place.ToString() : $"{Place} {Anchor}"
    };
}

/// <summary>How an entry began.</summary>
public sealed class OwnedItemBorn
{
    public OwnedItemBorn(OwnedItemBirth reason, long utcTicks, Guid by, Guid from = default, string detail = null)
    {
        Reason = reason;
        UtcTicks = utcTicks;
        By = by;
        From = from;
        Detail = detail ?? string.Empty;
    }

    public OwnedItemBirth Reason { get; }
    public long UtcTicks { get; }
    /// <summary>The identity of the player who caused it, or empty for the server.</summary>
    public Guid By { get; }
    /// <summary>The item this one replaced or copied, or empty.</summary>
    public Guid From { get; }
    public string Detail { get; }
}

/// <summary>How an entry's life ended.</summary>
public sealed class OwnedItemEnded
{
    public OwnedItemEnded(ItemRemovalReason reason, long utcTicks, Guid replacedBy = default, string detail = null)
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
/// One owned item's life in the server's ledger. Only <see cref="OwnedItemLedger"/> changes it;
/// everything else reads it.
/// </summary>
public sealed class OwnedItemEntry
{
    public OwnedItemEntry(Guid id, Guid owner, string prefab, OwnedItemWhere where, OwnedItemBorn born,
        int claimSlot = -1, bool shopStocked = false, OwnedItemEnded ended = null)
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
    public Guid Owner { get; }
    public string Prefab { get; }
    public OwnedItemWhere Where { get; internal set; }
    /// <summary>An essential's retrieval claim slot in its owner's inventory, or -1.</summary>
    public int ClaimSlot { get; internal set; }
    /// <summary>The item still has vanilla's Restock flag, so it counts against its owner's shop stock.</summary>
    public bool ShopStocked { get; internal set; }
    public OwnedItemBorn Born { get; }
    public OwnedItemEnded Ended { get; internal set; }

    public bool IsLive => Ended == null;

    public OwnedItemEntry Clone() => new(Id, Owner, Prefab, Where, Born, ClaimSlot, ShopStocked, Ended);

    public override string ToString() =>
        $"{Prefab} {Id:D} of {Owner:D}: {(IsLive ? Where.ToString() : "ended " + Ended.Reason)}";
}
