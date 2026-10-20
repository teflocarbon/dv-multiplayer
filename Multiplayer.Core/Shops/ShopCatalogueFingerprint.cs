using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Shops;

/// <summary>
/// A hash of a game's shops: the stock table's items in order, then each shop's items in order.
/// Shop requests name a shop by its index and an item by its index in that shop, and the shelf
/// layout picks its seed by shop index, so two games must have the same shops in the same order.
/// The host compares a joining client's fingerprint with its own and refuses the join on a
/// difference (a mod that adds shop items on one side only). See docs/design/shops.md.
/// </summary>
public readonly struct ShopCatalogueFingerprint : IEquatable<ShopCatalogueFingerprint>
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public ShopCatalogueFingerprint(ulong hash, int shops, int items)
    {
        Hash = hash;
        Shops = shops;
        Items = items;
    }

    public ulong Hash { get; }
    public int Shops { get; }
    public int Items { get; }

    /// <summary>
    /// Hashes the stock table's prefab names (<paramref name="items"/>, in their order) and each
    /// shop's prefab names (<paramref name="shops"/>, in their order). Order counts: it is what the
    /// indices on the wire refer to.
    /// </summary>
    public static ShopCatalogueFingerprint Compute(IReadOnlyList<string> items, IReadOnlyList<IReadOnlyList<string>> shops)
    {
        ulong hash = FnvOffset;
        items ??= Array.Empty<string>();
        shops ??= Array.Empty<IReadOnlyList<string>>();

        AddByte(ref hash, 1);
        AddList(ref hash, items);
        AddByte(ref hash, 2);
        AddInt(ref hash, shops.Count);
        foreach (IReadOnlyList<string> shop in shops)
            AddList(ref hash, shop ?? Array.Empty<string>());
        return new ShopCatalogueFingerprint(hash, shops.Count, items.Count);
    }

    /// <summary>
    /// Why a client's shops (<paramref name="client"/>) can't join this host's, for the player and
    /// the logs; null when they match.
    /// </summary>
    public string DescribeMismatch(ShopCatalogueFingerprint client)
    {
        if (Equals(client))
            return null;

        string counts = Shops == client.Shops && Items == client.Items
            ? $"Both have {Describe(this)}, but not the same ones or not in the same order."
            : $"The host has {Describe(this)}; you have {Describe(client)}.";
        return "Your shops don't match the host's, so purchases would go to the wrong item. " + counts +
               " Check that you both run the same mods that add shop items.";
    }

    public bool Equals(ShopCatalogueFingerprint other) =>
        Hash == other.Hash && Shops == other.Shops && Items == other.Items;

    public override bool Equals(object obj) => obj is ShopCatalogueFingerprint other && Equals(other);

    public override int GetHashCode() => Hash.GetHashCode();

    public override string ToString() => $"{Hash:X16} ({Shops} shops, {Items} items)";

    private static string Describe(ShopCatalogueFingerprint catalogue) =>
        $"{catalogue.Shops} shops and {catalogue.Items} shop items";

    private static void AddList(ref ulong hash, IReadOnlyList<string> names)
    {
        AddInt(ref hash, names.Count);
        foreach (string value in names)
        {
            // Length first, so ("ab", "c") and ("a", "bc") hash apart.
            string name = value ?? string.Empty;
            AddInt(ref hash, name.Length);
            foreach (char c in name)
            {
                AddByte(ref hash, (byte)c);
                AddByte(ref hash, (byte)(c >> 8));
            }
        }
    }

    private static void AddInt(ref ulong hash, int value)
    {
        for (int shift = 0; shift < 32; shift += 8)
            AddByte(ref hash, (byte)(value >> shift));
    }

    private static void AddByte(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= FnvPrime;
    }
}
