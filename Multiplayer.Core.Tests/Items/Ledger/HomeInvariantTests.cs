using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Tests.Items.Ledger;

/// <summary>The one-home check (docs/design/items/ledger.md, "The invariant check"; OI-LED-15).</summary>
[TestFixture]
public sealed class HomeInvariantTests
{
    private static readonly Guid Liam = ItemLedgerTests.G(0xA1);
    private static readonly Guid Bentgear = ItemLedgerTests.G(0xB2);
    private static readonly Guid Lantern = ItemLedgerTests.G(1);
    private static readonly Guid Radio = ItemLedgerTests.G(2);

    [Test]
    public void ALedgerAndWorldThatAgree_HaveNoViolations()
    {
        LedgerEntry[] entries =
        {
            Entry(Lantern, Home.TrainInterior("L-042")),
            Entry(Radio, Home.InSlot(Liam, 3), claimSlot: 3)
        };
        ObservedItem[] observed =
        {
            Live(Lantern, Home.TrainInterior("L-042")),
            Live(Radio, Home.InSlot(Liam, 3), VanillaLists.Inventory),
            Live(Guid.Empty, Home.InSlot(Liam, 4))   // an unowned mug beside it
        };

        Assert.That(HomeInvariant.Check(entries, observed, new InvariantContext { HostPlayer = Liam }), Is.Empty);
    }

    [Test]
    public void Rule1_AnItemNowhere_OrInTwoPlaces_IsOneHomeBroken()
    {
        LedgerEntry[] entries = { Entry(Lantern, Home.World), Entry(Radio, Home.LostAndFound) };
        ObservedItem[] observed =
        {
            Live(Lantern, Home.World),
            Row(Lantern, ObservedSource.LostAndFoundRow, Home.LostAndFound)
        };

        IReadOnlyList<InvariantViolation> found = HomeInvariant.Check(entries, observed);

        Assert.That(found.Select(v => (v.Rule, v.Id)), Is.EquivalentTo(new[]
        {
            (InvariantRule.OneHome, Lantern),
            (InvariantRule.OneHome, Radio)
        }));
        Assert.That(found.Single(v => v.Id == Lantern).Repair, Is.EqualTo(Home.World), "the copy the ledger names stays");
        Assert.That(found.Single(v => v.Id == Radio).Repair, Is.EqualTo(Home.LostAndFound), "a lost item goes to its owner's");
    }

    [Test]
    public void Rule1_AMissingEntry_IsNotLookedFor()
    {
        Assert.That(HomeInvariant.Check(new[] { Entry(Lantern, Home.Missing) }, Array.Empty<ObservedItem>()), Is.Empty);
    }

    [Test]
    public void Rule2_LiveStateElsewhere_IsLiveAgreesBroken_AndRepairsToTheLedger()
    {
        LedgerEntry[] entries =
        {
            Entry(Lantern, Home.TrainInterior("L-042")),
            Entry(Radio, Home.InSlot(Liam, 3))
        };
        ObservedItem[] observed =
        {
            Live(Lantern, Home.TrainInterior("L-007")),
            Live(Radio, Home.InSlot(Liam, 5))
        };

        IReadOnlyList<InvariantViolation> found = HomeInvariant.Check(entries, observed);

        Assert.That(found.Select(v => v.Rule), Is.All.EqualTo(InvariantRule.LiveAgrees));
        Assert.That(found.Select(v => v.Repair), Is.EquivalentTo(new Home?[] { Home.TrainInterior("L-042"), Home.InSlot(Liam, 3) }));
    }

    [Test]
    public void Rule2_APoseOrAHand_IsLiveStates()
    {
        // Which hand, and where on the ground, are live state's; only the kind and what it's in count.
        Assert.That(HomeInvariant.Agrees(Home.InHand(Liam, 1), Home.InHand(Liam, 2)), Is.True);
        Assert.That(HomeInvariant.Agrees(Home.InHand(Liam), Home.InHand(Bentgear)), Is.False);
        Assert.That(HomeInvariant.Agrees(Home.World, Home.World), Is.True);
        Assert.That(HomeInvariant.Agrees(Home.World, Home.TrainInterior("L-042")), Is.False);
        Assert.That(HomeInvariant.Agrees(Home.Installed("L-042"), Home.Installed(Home.WorldAnchor)), Is.False);
        Assert.That(HomeInvariant.Agrees(Home.Attached("L-042", true), Home.Attached("L-042", false)), Is.False);
    }

    [Test]
    public void Rule3_AnOwnedItemTheLedgerLacks_OrOneWhoseEntryEnded_IsAStray()
    {
        ItemLedger ledger = new();
        ledger.Register(new BirthRequest { Id = Lantern, Owner = OwnerRef.Player(Liam), Prefab = "Lantern", Home = Home.World });
        ledger.End(Lantern, ItemRemovalReason.DestroyedByGame);
        Guid unknown = ItemLedgerTests.G(9);
        ObservedItem[] observed =
        {
            Live(Lantern, Home.World),
            new() { Id = unknown, Source = ObservedSource.LostAndFoundRow, Home = Home.LostAndFound, StoreOwner = OwnerRef.Player(Bentgear) },
            Live(ItemLedgerTests.G(10), Home.World)   // an id but no owner: a found item nobody claimed
        };

        IReadOnlyList<InvariantViolation> found = HomeInvariant.Check(ledger.All(), observed);

        Assert.That(found.Select(v => (v.Rule, v.Id)), Is.EquivalentTo(new[]
        {
            (InvariantRule.NoStray, Lantern),
            (InvariantRule.NoStray, unknown)
        }));
    }

    [Test]
    public void Rule4_TwoItemsInOneSlot_OwnedOrNot_AndTwoClaimsOnOne_AreBroken()
    {
        LedgerEntry[] entries =
        {
            Entry(Lantern, Home.InSlot(Liam, 4)),
            Entry(Radio, Home.LostAndFound, claimSlot: 7),
            Entry(ItemLedgerTests.G(3), Home.LostAndFound, claimSlot: 7, prefab: "Map"),
            Entry(ItemLedgerTests.G(4), Home.LostAndFound, claimSlot: 7, owner: Bentgear, prefab: "Map")
        };
        ObservedItem[] observed =
        {
            Live(Lantern, Home.InSlot(Liam, 4)),
            Live(Guid.Empty, Home.InSlot(Liam, 4)),
            Row(Radio, ObservedSource.LostAndFoundRow, Home.LostAndFound),
            Row(ItemLedgerTests.G(3), ObservedSource.LostAndFoundRow, Home.LostAndFound),
            Row(ItemLedgerTests.G(4), ObservedSource.LostAndFoundRow, Home.LostAndFound)
        };

        IReadOnlyList<InvariantViolation> found = HomeInvariant.Check(entries, observed);

        Assert.That(found.Select(v => v.Rule), Is.EqualTo(new[] { InvariantRule.OneItemPerSlot, InvariantRule.OneItemPerSlot }));
        Assert.That(found[0].Detail, Does.Contain("slot 4"));
        Assert.That(found[1].Detail, Does.Contain("claim slot 7").And.Contain("claimed by 2"));
    }

    [Test]
    public void Rule5_VanillasListsFollowTheHomes()
    {
        LedgerEntry[] entries =
        {
            Entry(Lantern, Home.InSlot(Bentgear, 0)),
            Entry(Radio, Home.World),
            Entry(ItemLedgerTests.G(3), Home.InSlot(Liam, 1))
        };
        ObservedItem[] observed =
        {
            Live(Lantern, Home.InSlot(Bentgear, 0), VanillaLists.Inventory),           // a remote player's item in the host's list
            Live(Radio, Home.World, VanillaLists.World | VanillaLists.LostAndFound),     // ItemInMultipleStorages
            Live(ItemLedgerTests.G(3), Home.InSlot(Liam, 1), VanillaLists.Inventory)   // the host's own: fine
        };

        IReadOnlyList<InvariantViolation> found = HomeInvariant.Check(entries, observed, new InvariantContext { HostPlayer = Liam });

        Assert.That(found.Select(v => (v.Rule, v.Id)), Is.EquivalentTo(new[]
        {
            (InvariantRule.VanillaAgrees, Lantern),
            (InvariantRule.VanillaAgrees, Radio),
            (InvariantRule.VanillaAgrees, Radio)
        }));
    }

    [Test]
    public void Rule5_ADedicatedHostsOwnInventory_HoldsNothing()
    {
        LedgerEntry[] entries = { Entry(Lantern, Home.InSlot(Liam, 0)) };
        ObservedItem[] observed = { Live(Lantern, Home.InSlot(Liam, 0), VanillaLists.Inventory) };

        IReadOnlyList<InvariantViolation> found = HomeInvariant.Check(entries, observed, new InvariantContext { DedicatedHost = true });

        Assert.That(found.Single().Detail, Does.Contain("dedicated host"));
    }

    [Test]
    public void AColdRow_CountsAsTheItemsOneCopy()
    {
        // An offline player's inventory is data only (D12): the row is where the item is.
        LedgerEntry[] entries = { Entry(Radio, Home.InSlot(Bentgear, 3), claimSlot: 3, owner: Bentgear) };
        ObservedItem[] cold = { Row(Radio, ObservedSource.ColdRow, Home.InSlot(Bentgear, 3)) };
        ObservedItem[] alsoLive = { cold[0], Live(Radio, Home.InSlot(Bentgear, 3)) };

        Assert.That(HomeInvariant.Check(entries, cold), Is.Empty);
        Assert.That(HomeInvariant.Check(entries, alsoLive).Select(v => v.Rule),
            Is.EqualTo(new[] { InvariantRule.OneHome, InvariantRule.OneItemPerSlot }), "a cold row and an object: a duplicate");
    }

    [Test]
    public void FuzzRun_ALedgerOnlyEverCommitted_AndAWorldThatFollowedIt_NeverBreaksTheRules()
    {
        // Random requests, most of them refused; the world follows only what the ledger committed.
        Random random = new(4242);
        Guid[] players = { Liam, Bentgear, ItemLedgerTests.G(0xC3) };
        ItemLedger ledger = new();
        Dictionary<Guid, Home> world = new();
        List<Guid> ids = new();
        for (int i = 1; i <= 15; i++)
        {
            Registration made = ledger.Register(new BirthRequest
            {
                Id = ItemLedgerTests.G(i), Owner = OwnerRef.Player(players[i % 3]), Prefab = i % 4 == 0 ? "Map" : "Mug",
                Home = Home.LostAndFound, Essential = i % 4 == 0
            });
            ids.Add(made.Entry.Id);
            world[made.Entry.Id] = Home.LostAndFound;
        }
        int committed = 0;
        for (int step = 0; step < 3000; step++)
        {
            Guid id = ids[random.Next(ids.Count)];
            Home to = random.Next(5) switch
            {
                0 => Home.InHand(players[random.Next(3)]),
                1 => Home.World,
                2 => Home.LostAndFound,
                _ => Home.InSlot(players[random.Next(3)], random.Next(6))
            };
            Home from = random.Next(5) == 0 ? Home.World : world[id];
            if (ledger.Commit(id, from, to).Committed)
            {
                world[id] = to;
                committed++;
            }
            IReadOnlyList<InvariantViolation> found = HomeInvariant.Check(ledger.All(),
                world.Select(pair => Live(pair.Key, pair.Value)));
            Assert.That(found, Is.Empty, $"step {step}: " + string.Join("; ", found));
        }
        Assert.That(committed, Is.GreaterThan(300), "the run moved things");
    }

    private static LedgerEntry Entry(Guid id, Home home, int claimSlot = -1, Guid? owner = null, string prefab = null) =>
        new(id, OwnerRef.Player(owner ?? Liam), prefab ?? (claimSlot >= 0 ? "CommsRadio" : "Lantern"), home,
            new Birth(BirthReason.Purchased, 1, Liam), claimSlot);

    private static ObservedItem Live(Guid id, Home home, VanillaLists lists = VanillaLists.None) =>
        new() { Id = id, Source = ObservedSource.LiveObject, Home = home, Lists = lists };

    private static ObservedItem Row(Guid id, ObservedSource source, Home home) =>
        new() { Id = id, Source = source, Home = home };
}
