using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Essentials;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.SaveLoad;
using Multiplayer.Core.Tests.Items.Ledger;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Tests.Items.SaveLoad;

[TestFixture]
public sealed class OwnedItemReconcilerTests
{
    private static readonly Guid Liam = OwnedItemLedgerTests.G(0xA1);
    private static readonly Guid Bentgear = OwnedItemLedgerTests.G(0xB2);
    private static readonly Guid Lantern = OwnedItemLedgerTests.G(1);
    private static readonly Guid Radio = OwnedItemLedgerTests.G(2);

    [Test]
    public void AnItemWhereTheLedgerSays_IsKept()
    {
        OwnedItemEntry entry = Entry(Lantern, OwnedItemWhere.LostAndFound);

        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(new[] { entry },
            new[] { Hit(Lantern, OwnedItemStore.LostAndFound, OwnedItemWhere.LostAndFound) });

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Keep }));
    }

    [Test]
    public void AnItemSomewhereElse_TheStoreWinsAndTheLedgerFollows()
    {
        // A build without the ledger played the save: the lantern went into Lost and Found there.
        OwnedItemLedger ledger = LedgerWith(Entry(Lantern, OwnedItemWhere.World("car:L-042")));

        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(ledger.All(),
            new[] { Hit(Lantern, OwnedItemStore.LostAndFound, OwnedItemWhere.LostAndFound) });
        OwnedItemReconciler.ApplyToLedger(ledger, steps);

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Correct }));
        Assert.That(WhereOf(ledger, Lantern), Is.EqualTo(OwnedItemWhere.LostAndFound));
    }

    [Test]
    public void AnItemInTwoStores_KeepsTheCopyWhereTheLedgerSays()
    {
        OwnedItemEntry entry = Entry(Lantern, OwnedItemWhere.World());

        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(new[] { entry }, new[]
        {
            Hit(Lantern, OwnedItemStore.LostAndFound, OwnedItemWhere.LostAndFound),
            Hit(Lantern, OwnedItemStore.World, OwnedItemWhere.World())
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
        OwnedItemEntry entry = Entry(Lantern, OwnedItemWhere.Container(OwnedItemLedgerTests.G(77)));

        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(new[] { entry }, new[]
        {
            Hit(Lantern, OwnedItemStore.World, OwnedItemWhere.World()),
            Hit(Lantern, OwnedItemStore.LostAndFound, OwnedItemWhere.LostAndFound)
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
        OwnedItemLedger ledger = LedgerWith(Entry(Lantern, OwnedItemWhere.World()));

        IReadOnlyList<OwnedItemLoadStep> first = OwnedItemReconciler.Plan(ledger.All(), Array.Empty<OwnedItemStoreHit>());
        OwnedItemReconciler.ApplyToLedger(ledger, first);
        IReadOnlyList<OwnedItemLoadStep> second = OwnedItemReconciler.Plan(ledger.All(), Array.Empty<OwnedItemStoreHit>());
        IReadOnlyList<OwnedItemLoadStep> found = OwnedItemReconciler.Plan(ledger.All(),
            new[] { Hit(Lantern, OwnedItemStore.World, OwnedItemWhere.World()) });

        Assert.That(Actions(first), Is.EqualTo(new[] { OwnedItemLoadAction.MarkMissing }));
        Assert.That(WhereOf(ledger, Lantern), Is.EqualTo(OwnedItemWhere.Missing));
        Assert.That(ledger.IsLive(Lantern), Is.True, "missing is not ended");
        Assert.That(Actions(second), Is.EqualTo(new[] { OwnedItemLoadAction.Keep }));
        Assert.That(Actions(found), Is.EqualTo(new[] { OwnedItemLoadAction.Correct }));
    }

    [Test]
    public void AnEndedItemAStoreStillHas_LivesAgain()
    {
        OwnedItemLedger ledger = LedgerWith(Entry(Lantern, OwnedItemWhere.World()));
        ledger.End(Lantern, ItemRemovalReason.DestroyedByGame);

        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(ledger.All(),
            new[] { Hit(Lantern, OwnedItemStore.World, OwnedItemWhere.World("car:L-042")) });
        OwnedItemReconciler.ApplyToLedger(ledger, steps);

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Revive }));
        Assert.That(ledger.IsLive(Lantern), Is.True);
        Assert.That(WhereOf(ledger, Lantern), Is.EqualTo(OwnedItemWhere.World("car:L-042")));
    }

    [Test]
    public void AnEndedItemNoStoreHas_StaysEnded()
    {
        OwnedItemLedger ledger = LedgerWith(Entry(Lantern, OwnedItemWhere.World()));
        ledger.End(Lantern, ItemRemovalReason.MoneyDeposited);

        Assert.That(OwnedItemReconciler.Plan(ledger.All(), Array.Empty<OwnedItemStoreHit>()), Is.Empty);
    }

    [Test]
    public void ASaveWithoutALedger_IsMigrated_AndMigratingAgainChangesNothing()
    {
        Guid toolbox = OwnedItemLedgerTests.G(77);
        Guid cupInBox = OwnedItemLedgerTests.G(3);
        OwnedItemStoreHit[] stores =
        {
            Hit(Lantern, OwnedItemStore.World, OwnedItemWhere.World("car:L-042"), Liam, "Lantern", shopStocked: true),
            Hit(Radio, OwnedItemStore.Possession, OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 0), Liam, "CommsRadio"),
            Hit(cupInBox, OwnedItemStore.Container, OwnedItemWhere.Container(toolbox), Bentgear, "Mug"),
            // An unowned item a player carried: saved, but not the ledger's.
            Hit(OwnedItemLedgerTests.G(4), OwnedItemStore.Possession, OwnedItemWhere.With(Liam, OwnedItemHold.Hand), Guid.Empty, "Mug")
        };
        OwnedItemLedger ledger = new();

        IReadOnlyList<OwnedItemLoadStep> migration = OwnedItemReconciler.Plan(ledger.All(), stores);
        OwnedItemReconciler.ApplyToLedger(ledger, migration);
        IReadOnlyList<OwnedItemLoadStep> again = OwnedItemReconciler.Plan(ledger.All(), stores);

        Assert.That(Actions(migration), Is.EqualTo(Enumerable.Repeat(OwnedItemLoadAction.Register, 3)));
        Assert.That(ledger.LiveOwnedBy(Liam).Select(e => e.Id), Is.EquivalentTo(new[] { Lantern, Radio }));
        Assert.That(ledger.TryGet(cupInBox, out OwnedItemEntry cup) && cup.Owner == Bentgear, Is.True);
        Assert.That(cup.Born.Reason, Is.EqualTo(OwnedItemBirth.Migrated));
        Assert.That(ledger.CountLive(Liam, "Lantern", shopStocked: true), Is.EqualTo(1));
        Assert.That(ledger.TryGet(OwnedItemLedgerTests.G(4), out _), Is.False);
        Assert.That(Actions(again), Is.EqualTo(Enumerable.Repeat(OwnedItemLoadAction.Keep, 3)));
    }

    [Test]
    public void MigratedEssentials_KeepTheirClaimSlots_AndAClashIsSplit()
    {
        // An old save where the radio (with another player) and the map both claim slot 3.
        OwnedItemLedger ledger = new();
        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(ledger.All(), new[]
        {
            new OwnedItemStoreHit(Radio, OwnedItemStore.Possession, OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 3),
                Liam, "CommsRadio", claimSlot: 3),
            new OwnedItemStoreHit(OwnedItemLedgerTests.G(5), OwnedItemStore.Possession,
                OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 4), Liam, "Map", claimSlot: 3),
            new OwnedItemStoreHit(Lantern, OwnedItemStore.World, OwnedItemWhere.World(), Liam, "Lantern")
        });

        OwnedItemReconciler.ApplyToLedger(ledger, steps);

        Assert.That(ledger.TryGet(Radio, out OwnedItemEntry radio) && radio.ClaimSlot == 3, Is.True);
        Assert.That(ledger.TryGet(OwnedItemLedgerTests.G(5), out OwnedItemEntry map), Is.True);
        Assert.That(map.ClaimSlot, Is.EqualTo(EssentialClaimSlotPolicy.InventoryCapacity - 1));
        Assert.That(ledger.TryGet(Lantern, out OwnedItemEntry lantern) && lantern.ClaimSlot == -1, Is.True);
    }

    [Test]
    public void AnIdTwiceInAnOldSave_IsRegisteredOnce_FromTheStrongerStore()
    {
        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(Array.Empty<OwnedItemEntry>(), new[]
        {
            Hit(Lantern, OwnedItemStore.World, OwnedItemWhere.World(), Liam, "Lantern"),
            Hit(Lantern, OwnedItemStore.Container, OwnedItemWhere.Container(OwnedItemLedgerTests.G(77)), Liam, "Lantern")
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
        OwnedItemEntry entry = Entry(Lantern, OwnedItemWhere.With(Bentgear, OwnedItemHold.Inventory, 5));

        IReadOnlyList<OwnedItemLoadStep> steps = OwnedItemReconciler.Plan(new[] { entry },
            new[] { Hit(Lantern, OwnedItemStore.Possession, OwnedItemWhere.With(Bentgear, OwnedItemHold.Inventory, 5)) });

        Assert.That(Actions(steps), Is.EqualTo(new[] { OwnedItemLoadAction.Keep, OwnedItemLoadAction.RecoverToLostAndFound }));
        Assert.That(steps[1].Where, Is.EqualTo(OwnedItemWhere.LostAndFound));
        Assert.That(steps[1].Owner, Is.EqualTo(Liam));
    }

    [Test]
    public void EveryPlace_MapsToTheStoreThatHoldsIt()
    {
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.With(Liam, OwnedItemHold.Belt)), Is.EqualTo(OwnedItemStore.Possession));
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.World("static:shelf")), Is.EqualTo(OwnedItemStore.World));
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.Attached("car:L-042")), Is.EqualTo(OwnedItemStore.World));
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.Snapped("hanger")), Is.EqualTo(OwnedItemStore.World));
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.Container(Guid.Empty)), Is.EqualTo(OwnedItemStore.Container));
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.LostAndFound), Is.EqualTo(OwnedItemStore.LostAndFound));
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.Installed("car:L-042", 3)), Is.EqualTo(OwnedItemStore.Installed));
        Assert.That(OwnedItemReconciler.StoreFor(OwnedItemWhere.Missing), Is.Null);
    }

    private static OwnedItemEntry Entry(Guid id, OwnedItemWhere where) =>
        new(id, Liam, "Lantern", where, new OwnedItemBorn(OwnedItemBirth.Purchased, 1, Liam));

    private static OwnedItemLedger LedgerWith(params OwnedItemEntry[] entries)
    {
        OwnedItemLedger ledger = new();
        ledger.Restore(entries, null);
        return ledger;
    }

    private static OwnedItemStoreHit Hit(Guid id, OwnedItemStore store, OwnedItemWhere where, Guid? owner = null,
        string prefab = "Lantern", bool shopStocked = false) => new(id, store, where, owner ?? Liam, prefab, shopStocked);

    private static OwnedItemWhere WhereOf(OwnedItemLedger ledger, Guid id) =>
        ledger.TryGet(id, out OwnedItemEntry entry) ? entry.Where : default;

    private static OwnedItemLoadAction[] Actions(IEnumerable<OwnedItemLoadStep> steps) => steps.Select(s => s.Action).ToArray();
}
