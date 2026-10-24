using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;

namespace Multiplayer.Core.Items.SaveLoad;

/// <summary>What the load does with one owned item (docs/design/items/save-load.md, "Loading").</summary>
public enum LoadAction : byte
{
    /// <summary>Made from its saved row in its home: the world, a car, a shelf, a snap point, a car's end.</summary>
    MakeLive,
    /// <summary>In a player's inventory: kept as data (a cold row) until its holder joins (D12).</summary>
    KeepCold,
    /// <summary>In a container: a row of its container.</summary>
    IntoContainer,
    /// <summary>In its owner's Lost and Found: made there from its saved row.</summary>
    IntoLostAndFound,
    /// <summary>An installed gadget: vanilla's installed storage makes it.</summary>
    LeaveInstalled,
    /// <summary>It can't go home: made from its row (or fresh, with no row) in its owner's Lost and Found (D9).</summary>
    Recover,
    /// <summary>Its prefab is gone: its rows are kept verbatim and tried again at the next load (D9).</summary>
    Unloadable,
    /// <summary>
    /// In a tool's magazine: made into it once every other item is made, or a row of the tool's
    /// container if the tool is a row (containers.md, "Ammo in a magazine").
    /// </summary>
    IntoMagazine
}

/// <summary>One owned item's step at load.</summary>
public readonly struct LoadStep
{
    public LoadStep(Guid id, LoadAction action, Home home, LostItemReason reason = default, string detail = null)
    {
        Id = id;
        Action = action;
        Home = home;
        Reason = reason;
        Detail = detail ?? string.Empty;
    }

    public Guid Id { get; }
    public LoadAction Action { get; }
    /// <summary>Where it goes: its saved home, or Lost and Found for <see cref="LoadAction.Recover"/>.</summary>
    public Home Home { get; }
    /// <summary>The Lost and Found reason, for <see cref="LoadAction.Recover"/>.</summary>
    public LostItemReason Reason { get; }
    public string Detail { get; }

    public override string ToString() =>
        $"{Id:D}: {Action} {Home}{(Action == LoadAction.Recover ? $" ({Reason})" : "")}{(Detail.Length > 0 ? ": " + Detail : "")}";
}

/// <summary>What the server can tell the planner about the game it is loading into.</summary>
public sealed class LoadWorld
{
    /// <summary>Whether a prefab of this name can be made (vanilla's item list, then a mod's).</summary>
    public Func<string, bool> PrefabExists { get; set; } = _ => true;
    /// <summary>Whether a car with this GUID is in the game once the cars have loaded.</summary>
    public Func<string, bool> CarExists { get; set; } = _ => true;
}

/// <summary>
/// The load's plan for the owned items (docs/design/items/save-load.md, "Loading"): every live
/// entry's item is made in its home from its saved row, or left as data where its home is cold; one
/// that can't go home goes to its owner's Lost and Found, and one whose prefab is gone stays
/// <see cref="HomeKind.Unloadable"/>. There are no stores to reconcile: the ledger is the only place
/// an owned item's home and owner are saved, and each item's state is saved once beside it.
/// </summary>
public static class ItemLoadPlanner
{
    /// <param name="entries">The saved ledger, as restored.</param>
    /// <param name="saved">The ids that have a saved row (their state), whether it holds state or not.</param>
    public static IReadOnlyList<LoadStep> Plan(IEnumerable<LedgerEntry> entries, ISet<Guid> saved, LoadWorld world)
    {
        world ??= new LoadWorld();
        List<LoadStep> steps = new();
        foreach (LedgerEntry entry in (entries ?? Enumerable.Empty<LedgerEntry>()).Where(entry => entry != null && entry.IsLive))
            steps.Add(Step(entry, saved?.Contains(entry.Id) == true, world));
        return steps;
    }

    public static LoadStep Step(LedgerEntry entry, bool hasRow, LoadWorld world)
    {
        Home home = entry.Home;
        // An installed gadget's state is vanilla's (its installed storage); the mod saves only its entry.
        if (home.Kind == HomeKind.Installed)
            return home.Anchor == Home.WorldAnchor || world.CarExists(home.Anchor)
                ? new LoadStep(entry.Id, LoadAction.LeaveInstalled, home)
                : Recover(entry, LostItemReason.GadgetDestinationMissing, $"its car {home.Anchor} is gone");
        if (!world.PrefabExists(entry.Prefab))
            return new LoadStep(entry.Id, LoadAction.Unloadable, home, detail: $"no prefab called {entry.Prefab}");
        if (home.Kind == HomeKind.LostAndFound)
            return new LoadStep(entry.Id, LoadAction.IntoLostAndFound, home);
        if (!hasRow)
            return Recover(entry, LostItemReason.SaveRecovery, "its saved state is missing");
        switch (home.Kind)
        {
            case HomeKind.Slot:
            case HomeKind.Hand:
                // Never saved under a borrower (BR SL1): it goes to its owner.
                return entry.Owner.IsPlayer(home.Holder)
                    ? new LoadStep(entry.Id, LoadAction.KeepCold, home)
                    : Recover(entry, LostItemReason.SaveRecovery, $"saved with {home.Holder:D}, not its owner");
            case HomeKind.Container:
                return new LoadStep(entry.Id, LoadAction.IntoContainer, home);
            case HomeKind.Magazine:
                return new LoadStep(entry.Id, LoadAction.IntoMagazine, home);
            case HomeKind.TrainInterior:
            case HomeKind.Attached:
                return world.CarExists(home.Anchor)
                    ? new LoadStep(entry.Id, LoadAction.MakeLive, home)
                    : Recover(entry, LostItemReason.SaveRecovery, $"its car {home.Anchor} is gone");
            case HomeKind.World:
            case HomeKind.Shelf:
            case HomeKind.Snapped:
                return new LoadStep(entry.Id, LoadAction.MakeLive, home);
            default:
                return Recover(entry, LostItemReason.SaveRecovery, "saved with no home it can go to: " + home);
        }
    }

    private static LoadStep Recover(LedgerEntry entry, LostItemReason reason, string detail) =>
        new(entry.Id, LoadAction.Recover, Home.LostAndFound, reason, detail);
}
