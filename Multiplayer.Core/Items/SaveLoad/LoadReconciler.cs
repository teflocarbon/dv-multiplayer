using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.Ledger;

namespace Multiplayer.Core.Items.SaveLoad;

/// <summary>
/// The stores that hold an owned item's state. Declared in the order a copy is kept when one id is
/// found in several: Lost and Found first, loose in the world last.
/// </summary>
public enum OwnedItemStore : byte
{
    LostAndFound,
    Container,
    Installed,
    /// <summary>A player's saved hand, inventory or belt.</summary>
    Possession,
    /// <summary>The world, a train interior, a shelf, attached or snapped.</summary>
    World
}

/// <summary>An owned item a store holds after a load, as that store records it.</summary>
public readonly struct OwnedItemStoreHit
{
    public OwnedItemStoreHit(Guid id, OwnedItemStore store, Home where, Guid owner, string prefab,
        bool shopStocked = false, int claimSlot = -1)
    {
        Id = id;
        Store = store;
        Where = where;
        Owner = owner;
        Prefab = prefab ?? string.Empty;
        ShopStocked = shopStocked;
        ClaimSlot = claimSlot;
    }

    public Guid Id { get; }
    public OwnedItemStore Store { get; }
    public Home Where { get; }
    /// <summary>The owner the store records, for an item the ledger doesn't have yet.</summary>
    public Guid Owner { get; }
    public string Prefab { get; }
    public bool ShopStocked { get; }
    /// <summary>An essential's retrieval claim slot as the store records it, or -1.</summary>
    public int ClaimSlot { get; }
}

public enum OwnedItemLoadAction : byte
{
    /// <summary>Found where the ledger says.</summary>
    Keep,
    /// <summary>Found somewhere else: the store has the state, so its place wins and the ledger follows.</summary>
    Correct,
    /// <summary>An ended entry's item is in a store after all: it lives again where the store has it.</summary>
    Revive,
    /// <summary>A second copy of an id that is kept elsewhere: the server destroys this one.</summary>
    DropCopy,
    /// <summary>In no store: kept, shown as lost track of, and looked for again at the next load.</summary>
    MarkMissing,
    /// <summary>An owned item the ledger has never seen (a save from before the ledger): born Migrated.</summary>
    Register,
    /// <summary>
    /// Another player's item in a saved possession. Nobody is connected at load, so it breaks the rule
    /// that a departing player carries only their own and unowned items: the server sends it to its
    /// owner's Lost and Found.
    /// </summary>
    RecoverToLostAndFound
}

public readonly struct OwnedItemLoadStep
{
    public OwnedItemLoadStep(OwnedItemLoadAction action, Guid id, OwnedItemStore? store, Home where,
        Guid owner, string prefab, string reason, bool shopStocked = false, int claimSlot = -1)
    {
        ClaimSlot = claimSlot;
        Action = action;
        Id = id;
        Store = store;
        Where = where;
        Owner = owner;
        Prefab = prefab ?? string.Empty;
        Reason = reason ?? string.Empty;
        ShopStocked = shopStocked;
    }

    public OwnedItemLoadAction Action { get; }
    public Guid Id { get; }
    /// <summary>The store the step is about, or null for one that has no store (missing).</summary>
    public OwnedItemStore? Store { get; }
    /// <summary>Where the item is after the step.</summary>
    public Home Where { get; }
    public Guid Owner { get; }
    public string Prefab { get; }
    public string Reason { get; }
    public bool ShopStocked { get; }
    public int ClaimSlot { get; }

    public override string ToString() => $"{Action} {Prefab} {Id:D}" +
        (Store.HasValue ? $" ({Store})" : "") + (Reason.Length == 0 ? "" : ": " + Reason);
}

/// <summary>
/// Checks the ledger against what the stores hold after a load, before any player logs in. The
/// ledger decides where each item is; when it disagrees with the stores, the store that has the
/// item's state wins and the ledger is corrected, since a disagreement means the ledger is stale
/// (a build without it played the save). A save with no ledger is the case where every item is
/// new to it: that is the migration. See docs/design/owned-items.md, "Loading".
/// </summary>
public static class LoadReconciler
{
    public static IReadOnlyList<OwnedItemLoadStep> Plan(IEnumerable<LedgerEntry> ledger,
        IEnumerable<OwnedItemStoreHit> found)
    {
        Dictionary<Guid, List<OwnedItemStoreHit>> hitsById = new();
        foreach (OwnedItemStoreHit hit in found ?? Enumerable.Empty<OwnedItemStoreHit>())
        {
            if (hit.Id == Guid.Empty)
                continue;
            if (!hitsById.TryGetValue(hit.Id, out List<OwnedItemStoreHit> hits))
                hitsById.Add(hit.Id, hits = new List<OwnedItemStoreHit>());
            hits.Add(hit);
        }

        List<OwnedItemLoadStep> steps = new();
        HashSet<Guid> known = new();
        foreach (LedgerEntry entry in ledger ?? Enumerable.Empty<LedgerEntry>())
        {
            if (entry == null || !known.Add(entry.Id))
                continue;
            hitsById.TryGetValue(entry.Id, out List<OwnedItemStoreHit> hits);
            if (hits == null || hits.Count == 0)
            {
                if (entry.IsLive)
                    steps.Add(new OwnedItemLoadStep(entry.Where.Place == HomeKind.Missing
                            ? OwnedItemLoadAction.Keep
                            : OwnedItemLoadAction.MarkMissing, entry.Id, null, Home.Missing,
                        entry.Owner, entry.Prefab, entry.Where.Place == HomeKind.Missing
                            ? "still in no store"
                            : $"not in any store (the ledger had it {entry.Where})"));
                continue;
            }

            OwnedItemStoreHit kept = Choose(hits, entry.IsLive ? StoreFor(entry.Where) : null);
            if (!entry.IsLive)
                steps.Add(Step(OwnedItemLoadAction.Revive, kept, entry.Owner, entry.Prefab,
                    $"ended {entry.Ended.Reason}, but the {kept.Store} store has it"));
            else if (kept.Where == entry.Where)
                steps.Add(Step(OwnedItemLoadAction.Keep, kept, entry.Owner, entry.Prefab, null));
            else
                steps.Add(Step(OwnedItemLoadAction.Correct, kept, entry.Owner, entry.Prefab,
                    $"the ledger had it {entry.Where}"));
            AddCopies(steps, hits, kept, entry.Owner, entry.Prefab);
            AddRecovery(steps, kept, entry.Owner, entry.Prefab);
        }

        foreach (KeyValuePair<Guid, List<OwnedItemStoreHit>> pair in hitsById)
        {
            if (known.Contains(pair.Key))
                continue;
            OwnedItemStoreHit kept = Choose(pair.Value, null);
            if (kept.Owner == Guid.Empty)
                continue;
            steps.Add(Step(OwnedItemLoadAction.Register, kept, kept.Owner, kept.Prefab,
                $"in the {kept.Store} store, not in the ledger"));
            AddCopies(steps, pair.Value, kept, kept.Owner, kept.Prefab);
            AddRecovery(steps, kept, kept.Owner, kept.Prefab);
        }
        return steps;
    }

    /// <summary>
    /// Applies the steps that change only the ledger. <see cref="OwnedItemLoadAction.DropCopy"/> and
    /// <see cref="OwnedItemLoadAction.RecoverToLostAndFound"/> move items, so the server carries them
    /// out and then records the move.
    /// </summary>
    public static void ApplyToLedger(ItemLedger ledger, IEnumerable<OwnedItemLoadStep> steps)
    {
        foreach (OwnedItemLoadStep step in steps ?? Enumerable.Empty<OwnedItemLoadStep>())
        {
            switch (step.Action)
            {
                case OwnedItemLoadAction.Correct:
                case OwnedItemLoadAction.MarkMissing:
                    ledger.Move(step.Id, step.Where);
                    break;
                case OwnedItemLoadAction.Revive:
                    ledger.Revive(step.Id, step.Where);
                    break;
                case OwnedItemLoadAction.Register:
                    ledger.Register(new BirthRequest
                    {
                        Id = step.Id,
                        Owner = step.Owner,
                        Prefab = step.Prefab,
                        Where = step.Where,
                        Reason = BirthReason.Migrated,
                        Detail = step.Reason,
                        ShopStocked = step.ShopStocked,
                        Essential = step.ClaimSlot >= 0,
                        PreferredClaimSlot = step.ClaimSlot
                    });
                    break;
            }
        }
    }

    /// <summary>The store that holds an item in this place, or null for <see cref="HomeKind.Missing"/>.</summary>
    public static OwnedItemStore? StoreFor(Home where) => where.Place switch
    {
        HomeKind.With => OwnedItemStore.Possession,
        HomeKind.World or HomeKind.Attached or HomeKind.Snapped => OwnedItemStore.World,
        HomeKind.Container => OwnedItemStore.Container,
        HomeKind.LostAndFound => OwnedItemStore.LostAndFound,
        HomeKind.Installed => OwnedItemStore.Installed,
        _ => null
    };

    // The copy in the store the ledger names, else the first in the strongest store.
    private static OwnedItemStoreHit Choose(List<OwnedItemStoreHit> hits, OwnedItemStore? preferred)
    {
        if (preferred.HasValue)
            foreach (OwnedItemStoreHit hit in hits)
                if (hit.Store == preferred.Value)
                    return hit;
        OwnedItemStoreHit best = hits[0];
        foreach (OwnedItemStoreHit hit in hits)
            if (hit.Store < best.Store)
                best = hit;
        return best;
    }

    private static void AddCopies(List<OwnedItemLoadStep> steps, List<OwnedItemStoreHit> hits, OwnedItemStoreHit kept,
        Guid owner, string prefab)
    {
        bool skippedKept = false;
        foreach (OwnedItemStoreHit hit in hits)
        {
            if (!skippedKept && hit.Store == kept.Store && hit.Where == kept.Where)
            {
                skippedKept = true;
                continue;
            }
            steps.Add(Step(OwnedItemLoadAction.DropCopy, hit, owner, prefab,
                $"a second copy; the one in the {kept.Store} store is kept"));
        }
    }

    private static void AddRecovery(List<OwnedItemLoadStep> steps, OwnedItemStoreHit kept, Guid owner, string prefab)
    {
        if (kept.Where.IsWithPlayer && kept.Where.Holder != owner)
            steps.Add(new OwnedItemLoadStep(OwnedItemLoadAction.RecoverToLostAndFound, kept.Id, kept.Store,
                Home.LostAndFound, owner, prefab, $"saved with {kept.Where.Holder:D}, not its owner",
                kept.ShopStocked, kept.ClaimSlot));
    }

    private static OwnedItemLoadStep Step(OwnedItemLoadAction action, OwnedItemStoreHit hit, Guid owner, string prefab,
        string reason) => new(action, hit.Id, hit.Store, hit.Where, owner, prefab, reason, hit.ShopStocked, hit.ClaimSlot);
}
