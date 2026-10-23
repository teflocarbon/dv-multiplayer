using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Inventory;
using Multiplayer.Core.Items.Ledger;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Tests.Items.Ledger;

[TestFixture]
public sealed class ItemLedgerTests
{
    private static readonly Guid Liam = G(0xA1);
    private static readonly Guid Bentgear = G(0xB2);

    private long now;
    private int minted;
    private ItemLedger ledger;
    private List<LedgerChange> changes;

    [SetUp]
    public void SetUp()
    {
        now = 1000;
        minted = 0;
        ledger = new ItemLedger(() => G(0x10000 + ++minted), () => now);
        changes = new List<LedgerChange>();
        ledger.Changed += changes.Add;
    }

    [Test]
    public void ABoughtItem_KeepsTheIdItAlreadyHas()
    {
        Guid lantern = G(1);
        Registration result = Buy(lantern, "Lantern", Liam);

        Assert.That(result.Status, Is.EqualTo(RegistrationStatus.Registered));
        Assert.That(result.Entry.Id, Is.EqualTo(lantern));
        Assert.That(result.Entry.Born.Reason, Is.EqualTo(BirthReason.Purchased));
        Assert.That(ledger.OwnerOf(lantern), Is.EqualTo(P(Liam)));
        Assert.That(changes.Single().Kind, Is.EqualTo(LedgerChangeKind.Born));
    }

    [Test]
    public void AnItemWithoutAnId_GetsOneMinted()
    {
        Assert.That(Buy(Guid.Empty, "Lantern", Liam).Entry.Id, Is.EqualTo(G(0x10001)));
    }

    [Test]
    public void ACopyOfALiveItemsId_IsReminted_BornDuplicateFromTheOriginal()
    {
        // A copied save, a dupe, or an object cloned with its identity (OI-ID-18).
        Guid radio = G(1);
        Buy(radio, "CommsRadio", Liam);
        Assert.That(ledger.Commit(radio, Home.World, Home.InSlot(Liam, 0)).Committed, Is.True);

        Registration copy = Buy(radio, "CommsRadio", Bentgear);

        Assert.That(copy.Status, Is.EqualTo(RegistrationStatus.Reminted));
        Assert.That(copy.CopiedId, Is.EqualTo(radio));
        Assert.That(copy.Entry.Id, Is.Not.EqualTo(radio));
        Assert.That(copy.Entry.Born.Reason, Is.EqualTo(BirthReason.Duplicate));
        Assert.That(copy.Entry.Born.From, Is.EqualTo(radio));
        Assert.That(ledger.TryGet(radio, out LedgerEntry original) && original.Home == Home.InSlot(Liam, 0), Is.True,
            "the first keeps its id and its place");
    }

    [Test]
    public void AnEndedItemsId_IsNeverReused()
    {
        Guid tape = G(1);
        Buy(tape, "DuctTape", Liam);
        ledger.End(tape, ItemRemovalReason.DestroyedByGame);

        Assert.That(Buy(tape, "DuctTape", Liam).Status, Is.EqualTo(RegistrationStatus.Reminted));
        Assert.That(ledger.IsLive(tape), Is.False);
    }

    [Test]
    public void AnItemWithNoOwnerOrPrefab_IsRefused()
    {
        Assert.That(Buy(G(1), "Lantern", Guid.Empty).Status, Is.EqualTo(RegistrationStatus.Invalid));
        Assert.That(Buy(G(2), "", Liam).Status, Is.EqualTo(RegistrationStatus.Invalid));
        Assert.That(ledger.Count, Is.Zero);
    }

    [Test]
    public void Essentials_EachGetAClaimSlotOfTheirOwn_TheLowestFreeOffTheBelt()
    {
        // The issuer and the ledger agree (OI-ESS-02): a taken slot gives the lowest free one, never
        // the belt (OI-ESS-03).
        Registration radio = Issue(G(1), "CommsRadio", Liam, 3);
        Registration map = Issue(G(2), "Map", Liam, 3, home: Home.LostAndFound);
        Registration theirs = Issue(G(3), "Map", Bentgear, 3);

        Assert.That(radio.Entry.ClaimSlot, Is.EqualTo(3));
        Assert.That(map.Entry.ClaimSlot, Is.EqualTo(0));
        Assert.That(theirs.Entry.ClaimSlot, Is.EqualTo(3), "another owner's claims don't count");
        Assert.That(ledger.ChooseClaimSlot(P(Liam)), Is.EqualTo(1));
    }

    [Test]
    public void AClaim_NeverTakesTheBelt_NorASlotAnotherItemFills()
    {
        Buy(G(9), "Mug", Liam);
        ledger.Commit(G(9), Home.World, Home.InSlot(Liam, 0));
        ledger.SlotTakenOutside = (holder, slot, except) => holder == Liam && slot == 1;   // an unowned item

        Registration radio = Issue(G(1), "CommsRadio", Liam, InventorySlots.FirstBeltSlot, home: Home.LostAndFound);

        Assert.That(radio.Entry.ClaimSlot, Is.EqualTo(2));
    }

    [Test]
    public void APlainItem_HasNoClaimSlot()
    {
        Assert.That(Buy(G(1), "Lantern", Liam).Entry.ClaimSlot, Is.EqualTo(-1));
    }

    [Test]
    public void AnEssential_ClaimsTheSlotItsOwnerFirstStashesItIn()
    {
        Guid radio = ledger.Register(new BirthRequest
        {
            Id = G(1), Owner = P(Liam), Prefab = "CommsRadio", Home = Home.World,
            Reason = BirthReason.Claimed, Essential = true
        }).Entry.Id;
        Assert.That(ClaimOf(radio), Is.EqualTo(0), "it has one from the start");

        LedgerCommit stashed = ledger.Commit(radio, Home.World, Home.InSlot(Liam, 7));

        Assert.That(stashed.Committed, Is.True);
        Assert.That(ClaimOf(radio), Is.EqualTo(7), "the claim follows the owner's own placement");
        Assert.That(ledger.Commit(radio, Home.InSlot(Liam, 7), Home.InSlot(Liam, 34)).Committed, Is.True);
        Assert.That(ClaimOf(radio), Is.EqualTo(7), "but not onto the belt");
    }

    [Test]
    public void TheStolenRadiosSlot_IsKeptForIt_TillItComesBack()
    {
        // The clash EnsureDistinctClaimSlots repaired after the fact: with the ledger assigning
        // claims it can't arise. The slot is the radio's silhouette, so the map can't go there
        // while the radio is away; once the radio is back in another slot it can.
        Guid radio = Issue(G(1), "CommsRadio", Liam, 3).Entry.Id;
        Guid map = Issue(G(2), "Map", Liam, 7).Entry.Id;
        ledger.Commit(radio, Home.InSlot(Liam, 3), Home.InHand(Bentgear));

        Assert.That(ledger.Commit(map, Home.InSlot(Liam, 7), Home.InSlot(Liam, 3)).Refusal, Is.EqualTo("slot-reserved"));

        ledger.Commit(radio, Home.InHand(Bentgear), Home.InSlot(Liam, 12));
        Assert.That(ClaimOf(radio), Is.EqualTo(12));
        LedgerCommit moved = ledger.Commit(map, Home.InSlot(Liam, 7), Home.InSlot(Liam, 12 - 1));
        Assert.That(moved.Committed, Is.True);
        Assert.That(ClaimOf(map), Is.EqualTo(11));
        Assert.That(ledger.ClaimedSlots(P(Liam)), Is.EquivalentTo(new[] { 11, 12 }));
    }

    [Test]
    public void AnEssentialPutWhereAnotherClaims_TakesTheClaim_AndTheOtherTakesItsOld()
    {
        Guid radio = Issue(G(1), "CommsRadio", Liam, 3).Entry.Id;
        Guid map = Issue(G(2), "Map", Liam, 7).Entry.Id;
        // The map is on the belt, so its claim, 7, shows no silhouette; the radio goes there.
        ledger.Commit(map, Home.InSlot(Liam, 7), Home.InSlot(Liam, InventorySlots.FirstBeltSlot));
        ledger.Commit(radio, Home.InSlot(Liam, 3), Home.InSlot(Liam, 7));

        Assert.That(ClaimOf(radio), Is.EqualTo(7), "the radio took slot 7");
        Assert.That(ClaimOf(map), Is.EqualTo(3), "and the map's claim moved to the radio's old one");
        Assert.That(changes.Count(c => c.Kind == LedgerChangeKind.ClaimSlot), Is.EqualTo(1));
    }

    [Test]
    public void ACommit_FromAHomeTheItemIsntIn_IsRefused_AndChangesNothing()
    {
        Guid lantern = Buy(G(1), "Lantern", Liam).Entry.Id;
        changes.Clear();

        LedgerCommit stale = ledger.Commit(lantern, Home.InHand(Liam), Home.LostAndFound);

        Assert.That(stale.Committed, Is.False);
        Assert.That(stale.Refusal, Is.EqualTo("stale-home"));
        Assert.That(changes, Is.Empty);
        Assert.That(ledger.TryGetHome(lantern, out Home home) && home == Home.World, Is.True);
    }

    [Test]
    public void TwoItems_NeverShareASlot()
    {
        Guid a = Buy(G(1), "Mug", Liam).Entry.Id;
        Guid b = Buy(G(2), "Mug", Bentgear).Entry.Id;
        Assert.That(ledger.Commit(a, Home.World, Home.InSlot(Liam, 4)).Committed, Is.True);

        Assert.That(ledger.Commit(b, Home.World, Home.InSlot(Liam, 4)).Refusal, Is.EqualTo("slot-occupied"));
        ledger.SlotTakenOutside = (holder, slot, except) => slot == 5;
        Assert.That(ledger.Commit(b, Home.World, Home.InSlot(Liam, 5)).Refusal, Is.EqualTo("slot-occupied"),
            "an unowned item there counts too");
        Assert.That(ledger.Commit(b, Home.World, Home.InSlot(Liam, InventorySlots.Capacity)).Refusal, Is.EqualTo("slot-invalid"));
        Assert.That(ledger.Register(new BirthRequest
        {
            Id = G(3), Owner = P(Liam), Prefab = "Mug", Home = Home.InSlot(Liam, 4), Reason = BirthReason.Claimed
        }).Status, Is.EqualTo(RegistrationStatus.Refused));
    }

    [Test]
    public void AnItemRegisteredWhereItAlreadyIs_IsntInItsOwnWay()
    {
        // A first take or an issue: the item's record is in the slot before the ledger owns it.
        Guid map = G(1);
        ledger.SlotTakenOutside = (holder, slot, except) => holder == Liam && slot == 7 && except != map;

        Assert.That(Issue(map, "Map", Liam, 7).Status, Is.EqualTo(RegistrationStatus.Registered));
        Assert.That(Issue(G(2), "Map", Liam, 7, Home.InSlot(Liam, 7)).Refusal, Is.EqualTo("slot-occupied"));
    }

    [Test]
    public void TwoItemsInOneInventory_SwapSlots_InOneCommit_AndTheClaimsFollow()
    {
        Guid radio = Issue(G(1), "CommsRadio", Liam, 3).Entry.Id;
        Guid mug = Buy(G(2), "Mug", Liam).Entry.Id;
        Assert.That(ledger.Commit(mug, Home.World, Home.InSlot(Liam, 7)).Committed, Is.True);
        changes.Clear();

        LedgerCommit swapped = ledger.Swap(radio, Home.InSlot(Liam, 3), mug, Home.InSlot(Liam, 7));

        Assert.That(swapped.Committed, Is.True);
        Assert.That(ledger.TryGetHome(radio, out Home radioHome) && radioHome == Home.InSlot(Liam, 7), Is.True);
        Assert.That(ledger.TryGetHome(mug, out Home mugHome) && mugHome == Home.InSlot(Liam, 3), Is.True);
        Assert.That(ClaimOf(radio), Is.EqualTo(7), "the owner put it there, off the belt");
        Assert.That(changes.Count(change => change.Kind == LedgerChangeKind.Moved), Is.EqualTo(2));
    }

    [Test]
    public void ASwap_FromStaleHomes_OrAcrossInventories_IsRefused_AndChangesNothing()
    {
        Guid a = Buy(G(1), "Mug", Liam).Entry.Id;
        Guid b = Buy(G(2), "Mug", Liam).Entry.Id;
        Guid c = Buy(G(3), "Mug", Bentgear).Entry.Id;
        ledger.Commit(a, Home.World, Home.InSlot(Liam, 1));
        ledger.Commit(b, Home.World, Home.InSlot(Liam, 2));
        ledger.Commit(c, Home.World, Home.InSlot(Bentgear, 2));
        changes.Clear();

        Assert.That(ledger.Swap(a, Home.InSlot(Liam, 5), b, Home.InSlot(Liam, 2)).Refusal, Is.EqualTo("stale-home"));
        Assert.That(ledger.Swap(a, Home.InSlot(Liam, 1), c, Home.InSlot(Bentgear, 2)).Refusal, Is.EqualTo("slot-invalid"));
        Assert.That(changes, Is.Empty);
        Assert.That(ledger.TryGetHome(a, out Home home) && home == Home.InSlot(Liam, 1), Is.True);
    }

    [Test]
    public void ASwap_LeavesAThirdEssentialsClaimAlone()
    {
        Guid map = Issue(G(1), "Map", Liam, 4, home: Home.LostAndFound).Entry.Id;
        Guid a = Buy(G(2), "Mug", Liam).Entry.Id;
        Guid b = Buy(G(3), "Mug", Liam).Entry.Id;
        ledger.Commit(a, Home.World, Home.InSlot(Liam, 1));
        ledger.Commit(b, Home.World, Home.InSlot(Liam, 2));
        Assert.That(ClaimOf(map), Is.EqualTo(4));

        // Neither slot is the map's, so the swap goes; the map's silhouette stays in 4.
        Assert.That(ledger.Swap(a, Home.InSlot(Liam, 1), b, Home.InSlot(Liam, 2)).Committed, Is.True);
        Assert.That(ClaimOf(map), Is.EqualTo(4));
    }

    [Test]
    public void ACommit_RecordsWhereItWasAndWhy()
    {
        Guid lantern = Buy(G(1), "Lantern", Liam).Entry.Id;
        changes.Clear();

        ledger.Commit(lantern, Home.World, Home.TrainInterior("L-042"), new LedgerCause(ItemMoveReason.Dropped, Liam, "in the cab"));
        Assert.That(ledger.Commit(lantern, Home.TrainInterior("L-042"), Home.TrainInterior("L-042")).Committed, Is.True);

        Assert.That(changes, Has.Count.EqualTo(1), "no change, no event");
        Assert.That(changes[0].From, Is.EqualTo(Home.World));
        Assert.That(changes[0].Entry.Home, Is.EqualTo(Home.TrainInterior("L-042")));
        Assert.That(changes[0].Cause.Reason, Is.EqualTo(ItemMoveReason.Dropped));
        Assert.That(changes[0].Cause.By, Is.EqualTo(Liam));
    }

    [Test]
    public void ARefusedChange_LeavesTheLedgerAsItWas_FuzzRun()
    {
        // OI-LED-04: random changes, many of them stale or into a taken slot; after each, the ledger is
        // either moved by exactly the committed change or exactly as it was.
        Random random = new(20261024);
        Guid[] players = { Liam, Bentgear, G(0xC3) };
        List<Guid> ids = new();
        for (int i = 1; i <= 12; i++)
            ids.Add(i % 3 == 0 ? Issue(G(i), "Map", players[i % 3], -1, home: Home.LostAndFound).Entry.Id
                : Buy(G(i), "Mug", players[i % 3]).Entry.Id);
        Home RandomHome() => random.Next(6) switch
        {
            0 => Home.InSlot(players[random.Next(3)], random.Next(InventorySlots.Capacity + 1)),
            1 => Home.InHand(players[random.Next(3)]),
            2 => Home.World,
            3 => Home.LostAndFound,
            4 => Home.TrainInterior("L-" + random.Next(3)),
            _ => Home.InSlot(players[random.Next(3)], random.Next(4))
        };
        for (int step = 0; step < 4000; step++)
        {
            Guid id = ids[random.Next(ids.Count)];
            ledger.TryGetHome(id, out Home actual);
            Home from = random.Next(4) == 0 ? RandomHome() : actual;
            Home to = RandomHome();
            string before = ledger.Dump();
            LedgerCommit result = ledger.Commit(id, from, to);
            if (!result.Committed)
                Assert.That(ledger.Dump(), Is.EqualTo(before), $"step {step}: a refused change ({result.Refusal}) moved something");
            else
                Assert.That(ledger.TryGetHome(id, out Home after) && after == to, Is.True);
            IReadOnlyList<InvariantViolation> slotRules = HomeInvariant.Check(ledger.All(),
                ledger.All().Where(e => e.IsLive).Select(e => new ObservedItem { Id = e.Id, Home = e.Home }))
                .Where(v => v.Rule == InvariantRule.OneItemPerSlot).ToArray();
            Assert.That(slotRules, Is.Empty, $"step {step}: " + string.Join("; ", slotRules));
        }
    }

    [Test]
    public void AnEndedItem_CantMove_AndLeavesTheOwnersLists()
    {
        Guid lantern = Buy(G(1), "Lantern", Liam).Entry.Id;
        ledger.End(lantern, ItemRemovalReason.DestroyedByGame);

        Assert.That(ledger.Commit(lantern, Home.World, Home.LostAndFound).Refusal, Is.EqualTo("unknown-item"));
        Assert.That(ledger.LiveOwnedBy(P(Liam)), Is.Empty);
        Assert.That(ledger.OwnerOf(lantern).IsNone, Is.True);
    }

    [Test]
    public void UsingUpDuctTape_EndsItAndTheEmptyRollIsTheTapeOwners()
    {
        Guid tape = Buy(G(1), "DuctTape", Liam).Entry.Id;

        Registration roll = ledger.Replace(tape, ItemRemovalReason.DestroyedByGame, new BirthRequest
        {
            Id = G(2),
            Owner = P(Bentgear),   // whoever was holding it: the roll is still the tape owner's
            Prefab = "DuctTapeEmpty",
            Home = Home.InHand(Bentgear),
            By = Bentgear
        });

        Assert.That(roll.Entry.Owner, Is.EqualTo(P(Liam)));
        Assert.That(roll.Entry.Born.Reason, Is.EqualTo(BirthReason.Replaced));
        Assert.That(roll.Entry.Born.From, Is.EqualTo(tape));
        Assert.That(ledger.TryGet(tape, out LedgerEntry used) && !used.IsLive, Is.True);
        Assert.That(used.Ended.ReplacedBy, Is.EqualTo(G(2)));
    }

    [Test]
    public void ShopStock_CountsLiveItemsStillCarryingTheRestockFlag()
    {
        Buy(G(1), "Lantern", Liam, shopStocked: true);
        Buy(G(2), "Lantern", Liam, shopStocked: true);
        Buy(G(3), "Lantern", Bentgear, shopStocked: true);
        Buy(G(4), "Lighter", Liam, shopStocked: true);
        changes.Clear();

        Assert.That(ledger.CountLive(P(Liam), "Lantern", shopStocked: true), Is.EqualTo(2));

        ledger.SetShopStocked(G(1), false);   // a used garage key clears the flag
        ledger.End(G(2), ItemRemovalReason.DestroyedByGame);

        Assert.That(ledger.CountLive(P(Liam), "Lantern", shopStocked: true), Is.Zero);
        Assert.That(ledger.CountLive(P(Liam), "Lantern"), Is.EqualTo(1));
        Assert.That(changes.Select(c => c.Kind), Is.EqualTo(new[] { LedgerChangeKind.ShopStocked, LedgerChangeKind.Ended }));
    }

    [Test]
    public void WhatAPlayerGetsBackOnJoining_IsEverythingWithThem_OwnedByThemOrNot()
    {
        Guid radio = Buy(G(1), "CommsRadio", Liam).Entry.Id;
        Guid borrowed = Buy(G(2), "Lantern", Bentgear).Entry.Id;
        Guid away = Buy(G(3), "Lighter", Liam).Entry.Id;
        ledger.Commit(radio, Home.World, Home.InSlot(Liam, 0));
        ledger.Commit(borrowed, Home.World, Home.InHand(Liam));
        ledger.Commit(away, Home.World, Home.LostAndFound);

        Assert.That(ledger.LiveWith(Liam).Select(e => e.Id), Is.EquivalentTo(new[] { radio, borrowed }));
        Assert.That(ledger.LiveOwnedBy(P(Liam)).Select(e => e.Id), Is.EquivalentTo(new[] { radio, away }));
        Assert.That(ledger.OccupantOf(Liam, 0), Is.EqualTo(radio));
    }

    [Test]
    public void EndedEntries_ArePrunedBeyondAHundredPerOwner_OldestFirst()
    {
        for (int i = 1; i <= ItemLedger.EndedKeptPerOwner + 5; i++)
        {
            now = 1000 + i;
            Buy(G(i), "Banknote", Liam);
            ledger.End(G(i), ItemRemovalReason.MoneyDeposited);
        }
        Guid live = Buy(G(500), "Lantern", Liam).Entry.Id;
        Buy(G(501), "Banknote", Bentgear);
        ledger.End(G(501), ItemRemovalReason.MoneyDeposited);

        Assert.That(ledger.All().Count(e => e.Owner == P(Liam) && !e.IsLive), Is.EqualTo(ItemLedger.EndedKeptPerOwner));
        Assert.That(ledger.TryGet(G(1), out _), Is.False, "the oldest went");
        Assert.That(ledger.TryGet(G(6), out _), Is.True);
        Assert.That(ledger.IsLive(live), Is.True);
        Assert.That(ledger.TryGet(G(501), out _), Is.True, "another owner's history is theirs");
    }

    [Test]
    public void Restoring_KeepsTheFirstOfAnIdSavedTwice_AndRaisesNothing()
    {
        Birth born = new(BirthReason.Purchased, 5, Liam);
        LedgerEntry first = new(G(1), P(Liam), "Lantern", Home.LostAndFound, born);
        LedgerEntry twice = new(G(1), P(Bentgear), "Lantern", Home.World, born);
        LedgerEntry ownerless = new(G(2), OwnerRef.None, "Lantern", Home.World, born);

        IReadOnlyList<LedgerEntry> refused = ledger.Restore(new[] { first, twice, ownerless }, new[] { Liam });

        Assert.That(refused, Is.EqualTo(new[] { twice, ownerless }));
        Assert.That(ledger.OwnerOf(G(1)), Is.EqualTo(P(Liam)));
        Assert.That(ledger.HasStarterKit(Liam), Is.True);
        Assert.That(ledger.HasStarterKit(Bentgear), Is.False);
        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void Restoring_TwoItemsSavedInOneSlot_SendsTheSecondToLostAndFound_AndMovesClashingClaimsApart()
    {
        Birth born = new(BirthReason.Issued, 5, Liam);
        LedgerEntry radio = new(G(1), P(Liam), "CommsRadio", Home.InSlot(Liam, 3), born, claimSlot: 3);
        LedgerEntry map = new(G(2), P(Liam), "Map", Home.InSlot(Liam, 3), born, claimSlot: 3);
        List<Guid> displaced = new();

        ledger.Restore(new[] { radio, map }, null, displaced);

        Assert.That(displaced, Is.EqualTo(new[] { G(2) }));
        Assert.That(ledger.TryGetHome(G(2), out Home home) && home == Home.LostAndFound, Is.True);
        Assert.That(ClaimOf(G(1)), Is.EqualTo(3));
        Assert.That(ClaimOf(G(2)), Is.EqualTo(0));
    }

    [Test]
    public void ForgettingAStarterKit_ClearsTheMarker_Once()
    {
        ledger.MarkStarterKit(Liam);
        Assert.That(ledger.ForgetStarterKit(Liam), Is.True);
        Assert.That(ledger.ForgetStarterKit(Liam), Is.False);
        Assert.That(ledger.HasStarterKit(Liam), Is.False);
    }

    [Test]
    public void EntriesHandedOut_AreCopies()
    {
        Guid lantern = Buy(G(1), "Lantern", Liam).Entry.Id;
        ledger.TryGet(lantern, out LedgerEntry copy);
        ledger.Commit(lantern, Home.World, Home.LostAndFound);
        Assert.That(copy.Home, Is.EqualTo(Home.World));
    }

    [Test]
    public void AHome_KeepsOnlyTheFieldsItUses()
    {
        Assert.That(Home.From(HomeKind.TrainInterior, Liam, 4, 1, "L-042", true), Is.EqualTo(Home.TrainInterior("L-042")));
        Assert.That(Home.From(HomeKind.LostAndFound, Liam, 4, 1, "x", true), Is.EqualTo(Home.LostAndFound));
        Assert.That(Home.From(HomeKind.Installed, Guid.Empty, -1, 0, "L-042", false), Is.EqualTo(Home.Installed("L-042")));
        Assert.That(Home.From(HomeKind.Installed, Guid.Empty, -1, 0, "", false).Anchor, Is.EqualTo(Home.WorldAnchor),
            "a gadget on no car is on the world (OI-GAD-10)");
        Assert.That(Home.From(HomeKind.Slot, Liam, 4, 1, "x", true), Is.EqualTo(Home.InSlot(Liam, 4)));
        Assert.That(Home.From(HomeKind.Container, Guid.Empty, 2, 0, G(7).ToString("D"), false), Is.EqualTo(Home.Container(G(7), 2)));
    }

    [Test]
    public void Rekey_MovesOwnersHoldersCausesAndTheStarterKit_AndRaisesNothing()
    {
        Guid steam = G(0xC3);
        Guid lantern = G(1), radio = G(2), borrowed = G(3), theirs = G(4);
        Buy(lantern, "Lantern", Liam);
        Issue(radio, "CommsRadio", Liam, 2);
        Buy(borrowed, "Mug", Bentgear);
        ledger.Commit(borrowed, Home.World, Home.InHand(Liam));
        Buy(theirs, "Mug", Bentgear);
        ledger.MarkStarterKit(Liam);
        changes.Clear();

        RekeyResult result = ledger.Rekey(Liam, steam);

        Assert.That(result.Changed, Is.EqualTo(3));
        Assert.That(result.ToLostAndFound, Is.Empty);
        Assert.That(changes, Is.Empty);
        Assert.That(ledger.OwnerOf(lantern), Is.EqualTo(P(steam)));
        Assert.That(ledger.TryGet(lantern, out LedgerEntry bought) && bought.Born.By == steam, Is.True, "who caused it");
        Assert.That(ledger.TryGet(radio, out LedgerEntry issued) && issued.Home == Home.InSlot(steam, 2), Is.True);
        Assert.That(ledger.OwnerOf(borrowed), Is.EqualTo(P(Bentgear)), "someone else's item stays theirs");
        Assert.That(ledger.TryGet(borrowed, out LedgerEntry held) && held.Home.Holder == steam, Is.True);
        Assert.That(ledger.HasStarterKit(steam), Is.True);
        Assert.That(ledger.HasIdentity(Liam), Is.False);
        Assert.That(ledger.LiveOwnedBy(P(steam)), Has.Count.EqualTo(2));
        Assert.That(ledger.TryGet(theirs, out LedgerEntry untouched) && untouched.Owner == P(Bentgear), Is.True);
    }

    [Test]
    public void Rekey_OntoAnIdentityWithData_MergesIt_ASlotClashGoesToLostAndFound()
    {
        // OI-ID-19: the player played under the install GUID and under Steam. Nothing is skipped or
        // stranded under the old GUID.
        Guid steam = G(0xC3);
        Guid steamMug = Buy(G(1), "Mug", steam).Entry.Id;
        ledger.Commit(steamMug, Home.World, Home.InSlot(steam, 0));
        Guid steamRadio = Issue(G(2), "CommsRadio", steam, 3).Entry.Id;
        Guid guidMug = Buy(G(3), "Mug", Liam).Entry.Id;
        ledger.Commit(guidMug, Home.World, Home.InSlot(Liam, 0));
        Guid guidRadio = Issue(G(4), "CommsRadio", Liam, 3, home: Home.LostAndFound).Entry.Id;
        Guid guidLamp = Buy(G(5), "Lantern", Liam).Entry.Id;
        ledger.Commit(guidLamp, Home.World, Home.InSlot(Liam, 5));

        RekeyResult result = ledger.Rekey(Liam, steam);

        Assert.That(result.ToLostAndFound, Is.EqualTo(new[] { guidMug }));
        Assert.That(ledger.TryGetHome(guidMug, out Home mug) && mug == Home.LostAndFound, Is.True);
        Assert.That(ledger.TryGetHome(guidLamp, out Home lamp) && lamp == Home.InSlot(steam, 5), Is.True);
        Assert.That(ClaimOf(steamRadio), Is.EqualTo(3));
        Assert.That(ClaimOf(guidRadio), Is.Not.EqualTo(3).And.Not.EqualTo(0).And.Not.EqualTo(5).And.GreaterThanOrEqualTo(0));
        Assert.That(ledger.LiveOwnedBy(P(Liam)), Is.Empty);
        Assert.That(ledger.LiveOwnedBy(P(steam)), Has.Count.EqualTo(5));
    }

    [Test]
    public void Rekey_ToNobodyOrItself_ChangesNothing()
    {
        Buy(G(1), "Lantern", Liam);

        Assert.That(ledger.Rekey(Liam, Guid.Empty).Changed, Is.Zero);
        Assert.That(ledger.Rekey(Guid.Empty, Liam).Changed, Is.Zero);
        Assert.That(ledger.Rekey(Liam, Liam).Changed, Is.Zero);
        Assert.That(ledger.OwnerOf(G(1)), Is.EqualTo(P(Liam)));
    }

    [Test]
    public void HasIdentity_CountsAStarterKitAndAnItemHeldOrCaused()
    {
        Assert.That(ledger.HasIdentity(Liam), Is.False);
        ledger.MarkStarterKit(Liam);
        Assert.That(ledger.HasIdentity(Liam), Is.True);

        Buy(G(1), "Mug", Bentgear);
        ledger.Commit(G(1), Home.World, Home.InSlot(G(0xD4), 1));
        Assert.That(ledger.HasIdentity(G(0xD4)), Is.True, "a holder");
        Assert.That(ledger.HasIdentity(Guid.Empty), Is.False);
    }

    [Test]
    public void Dump_ListsEveryEntry_LiveFirst()
    {
        Buy(G(1), "Lantern", Liam);
        Buy(G(2), "Mug", Liam);
        ledger.End(G(2), ItemRemovalReason.DestroyedByGame);

        string[] lines = ledger.Dump().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Does.StartWith("Lantern"));
        Assert.That(lines[1], Does.Contain("ended DestroyedByGame"));
    }

    private Registration Buy(Guid id, string prefab, Guid owner, bool shopStocked = false) =>
        ledger.Register(new BirthRequest
        {
            Id = id,
            Owner = P(owner),
            Prefab = prefab,
            Home = Home.World,
            Reason = BirthReason.Purchased,
            By = owner,
            ShopStocked = shopStocked
        });

    private Registration Issue(Guid id, string prefab, Guid owner, int preferredSlot, Home? home = null) =>
        ledger.Register(new BirthRequest
        {
            Id = id,
            Owner = P(owner),
            Prefab = prefab,
            Home = home ?? Home.InSlot(owner, preferredSlot),
            Reason = BirthReason.Issued,
            Essential = true,
            PreferredClaimSlot = preferredSlot
        });

    private int ClaimOf(Guid id) => ledger.TryGet(id, out LedgerEntry entry) ? entry.ClaimSlot : -2;

    private static OwnerRef P(Guid id) => OwnerRef.Player(id);

    internal static Guid G(int n) => new($"00000000-0000-0000-0000-{n:x12}");
}
