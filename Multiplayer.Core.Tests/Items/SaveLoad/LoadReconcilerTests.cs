using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.SaveLoad;
using Multiplayer.Core.Tests.Items.Ledger;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Tests.Items.SaveLoad;

[TestFixture]
public sealed class LoadReconcilerTests
{
    private static readonly Guid Liam = ItemLedgerTests.G(0xA1);
    private static readonly Guid Bentgear = ItemLedgerTests.G(0xB2);
    private static readonly Guid Lantern = ItemLedgerTests.G(1);
    private static readonly Guid Radio = ItemLedgerTests.G(2);

    [Test]
    public void AnItemWhereTheLedgerSays_IsKept()
    {
        LedgerEntry entry = Entry(Lantern, Home.LostAndFound);

        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(new[] { entry },
            new[] { Hit(Lantern, OwnedItemStore.LostAndFound, Home.LostAndFound) });

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Keep }));
    }

    [Test]
    public void AnItemSomewhereElse_TheStoreWinsAndTheLedgerFollows()
    {
        // A build without the ledger played the save: the lantern went into Lost and Found there.
        ItemLedger ledger = LedgerWith(Entry(Lantern, Home.TrainInterior("L-042")));

        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(ledger.All(),
            new[] { Hit(Lantern, OwnedItemStore.LostAndFound, Home.LostAndFound) });
        LoadReconciler.ApplyToLedger(ledger, steps);

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Correct }));
        Assert.That(WhereOf(ledger, Lantern), Is.EqualTo(Home.LostAndFound));
    }

    [Test]
    public void AnItemInTwoStores_KeepsTheCopyWhereTheLedgerSays()
    {
        LedgerEntry entry = Entry(Lantern, Home.World);

        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(new[] { entry }, new[]
        {
            Hit(Lantern, OwnedItemStore.LostAndFound, Home.LostAndFound),
            Hit(Lantern, OwnedItemStore.World, Home.World)
        });

        Assert.That(steps.Select(s => (s.Action, s.Store)), Is.EqualTo(new (OwnedItemLoadAction, OwnedItemStore?)[]
        {
            (OwnedItemLoadAction.Keep, OwnedItemStore.World),
            (OwnedItemLoadAction.DropCopy, OwnedItemStore.LostAndFound)
        }));
    }

    [Test]
    public void AnItemInTwoStoresNeitherTheLedgersOwn_KeepsTheStrongerStore()
    {
        LedgerEntry entry = Entry(Lantern, Home.Container(ItemLedgerTests.G(77)));

        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(new[] { entry }, new[]
        {
            Hit(Lantern, OwnedItemStore.World, Home.World),
            Hit(Lantern, OwnedItemStore.LostAndFound, Home.LostAndFound)
        });

        Assert.That(steps.Select(s => (s.Action, s.Store)), Is.EqualTo(new (OwnedItemLoadAction, OwnedItemStore?)[]
        {
            (OwnedItemLoadAction.Correct, OwnedItemStore.LostAndFound),
            (OwnedItemLoadAction.DropCopy, OwnedItemStore.World)
        }));
    }

    [Test]
    public void AnItemInNoStore_IsMarkedMissingAndLookedForAgain()
    {
        ItemLedger ledger = LedgerWith(Entry(Lantern, Home.World));

        IReadOnlyList<OwnedItemLoadStep> first = LoadReconciler.Plan(ledger.All(), Array.Empty<OwnedItemStoreHit>());
        LoadReconciler.ApplyToLedger(ledger, first);
        IReadOnlyList<OwnedItemLoadStep> second = LoadReconciler.Plan(ledger.All(), Array.Empty<OwnedItemStoreHit>());
        IReadOnlyList<OwnedItemLoadStep> found = LoadReconciler.Plan(ledger.All(),
            new[] { Hit(Lantern, OwnedItemStore.World, Home.World) });

        Assert.That(Actions(first), Is.EqualTo(new[] { OwnedItemLoadAction.MarkMissing }));
        Assert.That(WhereOf(ledger, Lantern), Is.EqualTo(Home.Missing));
        Assert.That(ledger.IsLive(Lantern), Is.True, "missing is not ended");
        Assert.That(Actions(second), Is.EqualTo(new[] { OwnedItemLoadAction.Keep }));
        Assert.That(Actions(found), Is.EqualTo(new[] { OwnedItemLoadAction.Correct }));
    }

    [Test]
    public void AnEndedItemAStoreStillHas_LivesAgain()
    {
        ItemLedger ledger = LedgerWith(Entry(Lantern, Home.World));
        ledger.End(Lantern, ItemRemovalReason.DestroyedByGame);

        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(ledger.All(),
            new[] { Hit(Lantern, OwnedItemStore.World, Home.TrainInterior("L-042")) });
        LoadReconciler.ApplyToLedger(ledger, steps);

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Revive }));
        Assert.That(ledger.IsLive(Lantern), Is.True);
        Assert.That(WhereOf(ledger, Lantern), Is.EqualTo(Home.TrainInterior("L-042")));
    }

    [Test]
    public void AnEndedItemNoStoreHas_StaysEnded()
    {
        ItemLedger ledger = LedgerWith(Entry(Lantern, Home.World));
        ledger.End(Lantern, ItemRemovalReason.MoneyDeposited);

        Assert.That(LoadReconciler.Plan(ledger.All(), Array.Empty<OwnedItemStoreHit>()), Is.Empty);
    }

    [Test]
    public void ASaveWithoutALedger_IsMigrated_AndMigratingAgainChangesNothing()
    {
        Guid toolbox = ItemLedgerTests.G(77);
        Guid cupInBox = ItemLedgerTests.G(3);
        OwnedItemStoreHit[] stores =
        {
            Hit(Lantern, OwnedItemStore.World, Home.TrainInterior("L-042"), Liam, "Lantern", shopStocked: true),
            Hit(Radio, OwnedItemStore.Possession, Home.InSlot(Liam, 0), Liam, "CommsRadio"),
            Hit(cupInBox, OwnedItemStore.Container, Home.Container(toolbox), Bentgear, "Mug"),
            // An unowned item a player carried: saved, but not the ledger's.
            Hit(ItemLedgerTests.G(4), OwnedItemStore.Possession, Home.InHand(Liam), Guid.Empty, "Mug")
        };
        ItemLedger ledger = new();

        IReadOnlyList<OwnedItemLoadStep> migration = LoadReconciler.Plan(ledger.All(), stores);
        LoadReconciler.ApplyToLedger(ledger, migration);
        IReadOnlyList<OwnedItemLoadStep> again = LoadReconciler.Plan(ledger.All(), stores);

        Assert.That(Actions(migration), Is.EqualTo(Enumerable.Repeat(OwnedItemLoadAction.Register, 3)));
        Assert.That(ledger.LiveOwnedBy(OwnerRef.Player(Liam)).Select(e => e.Id), Is.EquivalentTo(new[] { Lantern, Radio }));
        Assert.That(ledger.TryGet(cupInBox, out LedgerEntry cup) && cup.Owner == OwnerRef.Player(Bentgear), Is.True);
        Assert.That(cup.Born.Reason, Is.EqualTo(BirthReason.Migrated));
        Assert.That(ledger.CountLive(OwnerRef.Player(Liam), "Lantern", shopStocked: true), Is.EqualTo(1));
        Assert.That(ledger.TryGet(ItemLedgerTests.G(4), out _), Is.False);
        Assert.That(Actions(again), Is.EqualTo(Enumerable.Repeat(OwnedItemLoadAction.Keep, 3)));
    }

    [Test]
    public void MigratedEssentials_KeepTheirClaimSlots_AndAClashIsSplit()
    {
        // An old save where the radio (with another player) and the map both claim slot 3.
        ItemLedger ledger = new();
        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(ledger.All(), new[]
        {
            new OwnedItemStoreHit(Radio, OwnedItemStore.Possession, Home.InSlot(Liam, 3),
                Liam, "CommsRadio", claimSlot: 3),
            new OwnedItemStoreHit(ItemLedgerTests.G(5), OwnedItemStore.Possession,
                Home.InSlot(Liam, 4), Liam, "Map", claimSlot: 3),
            new OwnedItemStoreHit(Lantern, OwnedItemStore.World, Home.World, Liam, "Lantern")
        });

        LoadReconciler.ApplyToLedger(ledger, steps);

        Assert.That(ledger.TryGet(Radio, out LedgerEntry radio) && radio.ClaimSlot == 3, Is.True);
        Assert.That(ledger.TryGet(ItemLedgerTests.G(5), out LedgerEntry map), Is.True);
        Assert.That(map.ClaimSlot, Is.EqualTo(4), "a clash falls back to the slot it was saved in");
        Assert.That(ledger.TryGet(Lantern, out LedgerEntry lantern) && lantern.ClaimSlot == -1, Is.True);
    }

    [Test]
    public void AnIdTwiceInAnOldSave_IsRegisteredOnce_FromTheStrongerStore()
    {
        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(Array.Empty<LedgerEntry>(), new[]
        {
            Hit(Lantern, OwnedItemStore.World, Home.World, Liam, "Lantern"),
            Hit(Lantern, OwnedItemStore.Container, Home.Container(ItemLedgerTests.G(77)), Liam, "Lantern")
        });

        Assert.That(steps.Select(s => (s.Action, s.Store)), Is.EqualTo(new (OwnedItemLoadAction, OwnedItemStore?)[]
        {
            (OwnedItemLoadAction.Register, OwnedItemStore.Container),
            (OwnedItemLoadAction.DropCopy, OwnedItemStore.World)
        }));
    }

    [Test]
    public void SomeoneElsesItemInASavedInventory_GoesToItsOwnersLostAndFound()
    {
        // Today's gap: Bentgear left with Liam's lantern while Liam was offline, so it stayed in
        // Bentgear's saved inventory.
        LedgerEntry entry = Entry(Lantern, Home.InSlot(Bentgear, 5));

        IReadOnlyList<OwnedItemLoadStep> steps = LoadReconciler.Plan(new[] { entry },
            new[] { Hit(Lantern, OwnedItemStore.Possession, Home.InSlot(Bentgear, 5)) });

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Keep, OwnedItemLoadAction.RecoverToLostAndFound }));
        Assert.That(steps[1].Where, Is.EqualTo(Home.LostAndFound));
        Assert.That(steps[1].Owner, Is.EqualTo(Liam));
    }

    [Test]
    public void EveryPlace_MapsToTheStoreThatHoldsIt()
    {
        Assert.That(LoadReconciler.StoreFor(Home.InSlot(Liam, 34)), Is.EqualTo(OwnedItemStore.Possession));
        Assert.That(LoadReconciler.StoreFor(Home.Shelf("shelf")), Is.EqualTo(OwnedItemStore.World));
        Assert.That(LoadReconciler.StoreFor(Home.Attached("L-042", true)), Is.EqualTo(OwnedItemStore.World));
        Assert.That(LoadReconciler.StoreFor(Home.Snapped("hanger")), Is.EqualTo(OwnedItemStore.World));
        Assert.That(LoadReconciler.StoreFor(Home.Container(Guid.Empty)), Is.EqualTo(OwnedItemStore.Container));
        Assert.That(LoadReconciler.StoreFor(Home.LostAndFound), Is.EqualTo(OwnedItemStore.LostAndFound));
        Assert.That(LoadReconciler.StoreFor(Home.Installed("L-042")), Is.EqualTo(OwnedItemStore.Installed));
        Assert.That(LoadReconciler.StoreFor(Home.Missing), Is.Null);
    }

    private static LedgerEntry Entry(Guid id, Home where) =>
        new(id, OwnerRef.Player(Liam), "Lantern", where, new Birth(BirthReason.Purchased, 1, Liam));

    private static ItemLedger LedgerWith(params LedgerEntry[] entries)
    {
        ItemLedger ledger = new();
        ledger.Restore(entries, null);
        return ledger;
    }

    private static OwnedItemStoreHit Hit(Guid id, OwnedItemStore store, Home where, Guid? owner = null,
        string prefab = "Lantern", bool shopStocked = false) => new(id, store, where, owner ?? Liam, prefab, shopStocked);

    private static Home WhereOf(ItemLedger ledger, Guid id) =>
        ledger.TryGet(id, out LedgerEntry entry) ? entry.Home : default;

    private static OwnedItemLoadAction[] Actions(IEnumerable<OwnedItemLoadStep> steps) => steps.Select(s => s.Action).ToArray();
}
