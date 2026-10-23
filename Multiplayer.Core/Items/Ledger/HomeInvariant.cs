using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items.Ledger;

/// <summary>Where the server found an item: a live object, or a row a cold home keeps.</summary>
public enum ObservedSource : byte
{
    LiveObject,
    /// <summary>A cold inventory row: an offline (or, on the server, remote) player's item kept as data.</summary>
    ColdRow,
    LostAndFoundRow,
    ContainerRow
}

/// <summary>Vanilla's <c>StorageController</c> lists an item is in.</summary>
[Flags]
public enum VanillaLists : byte
{
    None = 0,
    Inventory = 1,
    World = 2,
    LostAndFound = 4,
    Installed = 8
}

/// <summary>One copy of an item the server can see, for <see cref="HomeInvariant.Check"/>.</summary>
public sealed class ObservedItem
{
    /// <summary>The item's lifelong id; empty for a scene-authored item nobody owns.</summary>
    public Guid Id;
    /// <summary>What to call it in a violation (its prefab and NetId).</summary>
    public string Name = string.Empty;
    public ObservedSource Source;
    /// <summary>Where this copy is, as a home.</summary>
    public Home Home;
    /// <summary>The owner a store's own row names (a Lost and Found entry, a container row), or none.</summary>
    public OwnerRef StoreOwner;
    public VanillaLists Lists;
}

public enum InvariantRule : byte
{
    /// <summary>Every live entry's item is in exactly one place.</summary>
    OneHome = 1,
    /// <summary>Live state agrees with the ledger's home.</summary>
    LiveAgrees = 2,
    /// <summary>No owned item without a live entry, and no ended entry's item.</summary>
    NoStray = 3,
    /// <summary>One item per holder slot, and one claim per owner slot.</summary>
    OneItemPerSlot = 4,
    /// <summary>Vanilla's storage lists agree with the homes.</summary>
    VanillaAgrees = 5
}

public readonly struct InvariantViolation
{
    public InvariantViolation(InvariantRule rule, Guid id, string detail, Home? repair = null)
    {
        Rule = rule;
        Id = id;
        Detail = detail ?? string.Empty;
        Repair = repair;
    }

    public InvariantRule Rule { get; }
    public Guid Id { get; }
    public string Detail { get; }
    /// <summary>Where a Release build puts the item (D7): its ledger home, or its owner's Lost and Found.</summary>
    public Home? Repair { get; }

    public override string ToString() => $"rule {(int)Rule} {Rule}: {Detail}";
}

public sealed class InvariantContext
{
    /// <summary>The server has no player of its own: nothing may be in its vanilla inventory.</summary>
    public bool DedicatedHost;
    /// <summary>The host's own player, whose inventory vanilla's inventory list mirrors; empty on a dedicated host.</summary>
    public Guid HostPlayer;
}

/// <summary>
/// The one-home check (docs/design/items/ledger.md, "The invariant check"; D7, OI-LED-15): takes the
/// ledger and what the server observes, and returns every violation of the guarantee. Pure: the
/// server gathers the observations, runs it at load, before every save and every few seconds in
/// Debug, and scenarios run it after every run.
/// </summary>
public static class HomeInvariant
{
    public static IReadOnlyList<InvariantViolation> Check(IEnumerable<LedgerEntry> entries,
        IEnumerable<ObservedItem> observed, InvariantContext context = null)
    {
        context ??= new InvariantContext();
        List<InvariantViolation> violations = new();
        LedgerEntry[] all = (entries ?? Enumerable.Empty<LedgerEntry>()).Where(entry => entry != null).ToArray();
        Dictionary<Guid, LedgerEntry> byId = new();
        foreach (LedgerEntry entry in all)
            byId[entry.Id] = entry;
        ObservedItem[] items = (observed ?? Enumerable.Empty<ObservedItem>()).Where(item => item != null).ToArray();
        ILookup<Guid, ObservedItem> copies = items.Where(item => item.Id != Guid.Empty).ToLookup(item => item.Id);

        // Rules 1 and 2: each live entry's item is in its home, and only there.
        foreach (LedgerEntry entry in all.Where(entry => entry.IsLive && entry.Home.Kind != HomeKind.Unloadable))
        {
            ObservedItem[] found = copies[entry.Id].ToArray();
            if (found.Length == 0)
                violations.Add(new InvariantViolation(InvariantRule.OneHome, entry.Id,
                    $"{entry}: no object or row carries it", Home.LostAndFound));
            else if (found.Length > 1)
                violations.Add(new InvariantViolation(InvariantRule.OneHome, entry.Id,
                    $"{entry}: {found.Length} copies ({string.Join(", ", found.Select(Describe))})", entry.Home));
            else if (!Agrees(entry.Home, found[0].Home))
                violations.Add(new InvariantViolation(InvariantRule.LiveAgrees, entry.Id,
                    $"{entry}: found {Describe(found[0])}", entry.Home));
        }

        // Rule 3: nothing owned outside the ledger, nothing of an ended entry.
        foreach (ObservedItem item in items.Where(item => item.Id != Guid.Empty))
        {
            byId.TryGetValue(item.Id, out LedgerEntry entry);
            if (entry != null && !entry.IsLive)
                violations.Add(new InvariantViolation(InvariantRule.NoStray, item.Id,
                    $"{Describe(item)}: its entry ended ({entry.Ended.Reason})"));
            else if (entry == null && !item.StoreOwner.IsNone)
                violations.Add(new InvariantViolation(InvariantRule.NoStray, item.Id,
                    $"{Describe(item)}: owned by {item.StoreOwner} but not in the ledger"));
        }

        // Rule 4: one item per holder slot, owned and unowned together; one claim per owner slot.
        foreach (IGrouping<(Guid, int), ObservedItem> slot in items
                     .Where(item => item.Home.Kind == HomeKind.Slot)
                     .GroupBy(item => (item.Home.Holder, item.Home.Slot))
                     .Where(group => group.Count() > 1))
            violations.Add(new InvariantViolation(InvariantRule.OneItemPerSlot, slot.First().Id,
                $"slot {slot.Key.Item2} of {slot.Key.Item1:D} holds {slot.Count()}: {string.Join(", ", slot.Select(Describe))}"));
        foreach (IGrouping<(OwnerRef, int), LedgerEntry> claim in all
                     .Where(entry => entry.IsLive && entry.ClaimSlot >= 0)
                     .GroupBy(entry => (entry.Owner, entry.ClaimSlot))
                     .Where(group => group.Count() > 1))
            violations.Add(new InvariantViolation(InvariantRule.OneItemPerSlot, claim.First().Id,
                $"claim slot {claim.Key.Item2} of {claim.Key.Item1} is claimed by {claim.Count()}: " +
                string.Join(", ", claim.Select(entry => entry.Prefab + " " + entry.Id.ToString("D")))));

        // Rule 5: vanilla's lists follow the homes, never the other way.
        foreach (ObservedItem item in items.Where(item => item.Lists != VanillaLists.None))
        {
            Home home = byId.TryGetValue(item.Id, out LedgerEntry entry) && entry.IsLive ? entry.Home : item.Home;
            if (Count(item.Lists) > 1)
                violations.Add(new InvariantViolation(InvariantRule.VanillaAgrees, item.Id,
                    $"{Describe(item)} is in several vanilla lists ({item.Lists}): ItemInMultipleStorages"));
            if (item.Lists.HasFlag(VanillaLists.Inventory) && context.DedicatedHost)
                violations.Add(new InvariantViolation(InvariantRule.VanillaAgrees, item.Id,
                    $"{Describe(item)} is in the dedicated host's own inventory"));
            else if (item.Lists.HasFlag(VanillaLists.Inventory) &&
                     !(home.IsWithPlayer && home.Holder == context.HostPlayer && context.HostPlayer != Guid.Empty))
                violations.Add(new InvariantViolation(InvariantRule.VanillaAgrees, item.Id,
                    $"{Describe(item)} is in vanilla's inventory list, but its home is {home}"));
            if (item.Lists.HasFlag(VanillaLists.LostAndFound) && entry != null && entry.IsLive &&
                home.Kind != HomeKind.LostAndFound)
                violations.Add(new InvariantViolation(InvariantRule.VanillaAgrees, item.Id,
                    $"{Describe(item)} is in vanilla's Lost and Found list, but its home is {home}"));
        }
        return violations;
    }

    /// <summary>
    /// Whether a copy is where the ledger says. A loose item's pose is live state's, so only its kind
    /// and what it is in or on count; a hand item's hand is how it is held, not where.
    /// </summary>
    public static bool Agrees(Home ledger, Home observed) => ledger.Kind switch
    {
        HomeKind.Hand => observed.Kind == HomeKind.Hand && observed.Holder == ledger.Holder,
        HomeKind.World or HomeKind.TrainInterior or HomeKind.Shelf or HomeKind.Snapped =>
            observed.Kind == ledger.Kind && string.Equals(observed.Anchor, ledger.Anchor, StringComparison.Ordinal),
        _ => observed == ledger
    };

    private static string Describe(ObservedItem item) =>
        $"{(item.Name.Length > 0 ? item.Name : item.Id.ToString("D"))} as {item.Source} at {item.Home}";

    private static int Count(VanillaLists lists)
    {
        int count = 0;
        for (byte bits = (byte)lists; bits != 0; bits &= (byte)(bits - 1))
            count++;
        return count;
    }
}
