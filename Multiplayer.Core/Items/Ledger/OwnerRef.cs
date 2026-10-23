using System;

namespace Multiplayer.Core.Items.Ledger;

/// <summary>What kind of thing owns an item. Saved by name, append-only.</summary>
public enum OwnerKind : byte
{
    None,
    Player
    // A company, later (docs/design/item-refactor.md, "Rows outside this refactor"): the type has
    // room for it and nothing else does yet.
}

/// <summary>
/// Who owns an item: a player's identity, never a per-session number. An offline owner is an
/// identity with no session (docs/design/items/ledger.md, "Owners").
/// </summary>
public readonly struct OwnerRef : IEquatable<OwnerRef>
{
    private OwnerRef(OwnerKind kind, Guid id)
    {
        Kind = id == Guid.Empty ? OwnerKind.None : kind;
        Id = Kind == OwnerKind.None ? Guid.Empty : id;
    }

    public OwnerKind Kind { get; }
    public Guid Id { get; }

    public static OwnerRef None => default;
    public static OwnerRef Player(Guid identity) => new(OwnerKind.Player, identity);

    public bool IsNone => Kind == OwnerKind.None;
    /// <summary>The owning player's identity, or empty when nobody, or no player, owns it.</summary>
    public Guid PlayerId => Kind == OwnerKind.Player ? Id : Guid.Empty;
    public bool IsPlayer(Guid identity) => Kind == OwnerKind.Player && identity != Guid.Empty && Id == identity;

    public bool Equals(OwnerRef other) => Kind == other.Kind && Id == other.Id;
    public override bool Equals(object obj) => obj is OwnerRef other && Equals(other);
    public override int GetHashCode() => ((int)Kind * 397) ^ Id.GetHashCode();
    public static bool operator ==(OwnerRef a, OwnerRef b) => a.Equals(b);
    public static bool operator !=(OwnerRef a, OwnerRef b) => !a.Equals(b);

    public override string ToString() => Kind == OwnerKind.None ? "nobody" : Id.ToString("D");
}
