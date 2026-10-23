using System;

namespace Multiplayer.Core.Items.Ledger;

/// <summary>
/// A player's relation to an item, which is all the wire tells a player about an item's owner
/// (refactor D3; docs/design/items/wire.md, "Naming a player"). Byte-sized on the wire, append-only.
/// </summary>
public enum ItemRelation : byte
{
    /// <summary>Nobody owns it.</summary>
    Nobodys,
    /// <summary>The recipient owns it.</summary>
    Mine,
    /// <summary>Someone else owns it, online or not: red in every inventory but the owner's.</summary>
    SomeoneElses
}

public static class ItemRelations
{
    /// <summary>
    /// The relation of the player with identity <paramref name="viewer"/> to an item owned by
    /// <paramref name="owner"/>. No viewer (a dedicated host's own world) sees every owned item as
    /// someone else's.
    /// </summary>
    public static ItemRelation Of(Guid owner, Guid viewer) =>
        owner == Guid.Empty ? ItemRelation.Nobodys
        : viewer != Guid.Empty && owner == viewer ? ItemRelation.Mine
        : ItemRelation.SomeoneElses;

    public static bool IsOwned(this ItemRelation relation) => relation != ItemRelation.Nobodys;
}
