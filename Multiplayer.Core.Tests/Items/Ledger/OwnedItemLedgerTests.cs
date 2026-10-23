using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Essentials;
using Multiplayer.Core.Items.Ledger;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Tests.Items.Ledger;

[TestFixture]
public sealed class OwnedItemLedgerTests
{
    private static readonly Guid Liam = G(0xA1);
    private static readonly Guid Bentgear = G(0xB2);

    private long now;
    private int minted;
    private OwnedItemLedger ledger;
    private List<OwnedItemChange> changes;

    [SetUp]
    public void SetUp()
    {
        now = 1000;
        minted = 0;
        ledger = new OwnedItemLedger(() => G(0x10000 + ++minted), () => now);
        changes = new List<OwnedItemChange>();
        ledger.Changed += changes.Add;
    }

    [Test]
    public void ABoughtItem_KeepsTheIdItAlreadyHas()
    {
        Guid lantern = G(1);
        OwnedItemRegistration result = Buy(lantern, "Lantern", Liam);

        Assert.That(result.Status, Is.EqualTo(OwnedItemRegistrationStatus.Registered));
        Assert.That(result.Entry.Id, Is.EqualTo(lantern));
        Assert.That(result.Entry.Born.Reason, Is.EqualTo(OwnedItemBirth.Purchased));
        Assert.That(ledger.OwnerOf(lantern), Is.EqualTo(Liam));
        Assert.That(changes.Single().Kind, Is.EqualTo(OwnedItemChangeKind.Born));
    }

    [Test]
    public void AnItemWithoutAnId_GetsOneMinted()
    {
        OwnedItemRegistration result = Buy(Guid.Empty, "Lantern", Liam);

        Assert.That(result.Entry.Id, Is.EqualTo(G(0x10001)));
    }

    [Test]
    public void ACopyOfALiveItemsId_IsReminted_AndTheOriginalKeepsIt()
    {
        // A copied save, a dupe, or an object cloned with its identity: Instantiate copies the
        // serialized PersistentWorldItemIdentity, so the clone arrives with the original's id.
        Guid radio = G(1);
        Buy(radio, "CommsRadio", Liam);
        ledger.Move(radio, OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 0));

        OwnedItemRegistration copy = Buy(radio, "CommsRadio", Liam);

        Assert.That(copy.Status, Is.EqualTo(OwnedItemRegistrationStatus.Reminted));
        Assert.That(copy.CopiedId, Is.EqualTo(radio));
        Assert.That(copy.Entry.Id, Is.Not.EqualTo(radio));
        Assert.That(copy.Entry.Born.Reason, Is.EqualTo(OwnedItemBirth.Duplicate));
        Assert.That(copy.Entry.Born.From, Is.EqualTo(radio));
        Assert.That(ledger.TryGet(radio, out OwnedItemEntry original), Is.True);
        Assert.That(original.Where, Is.EqualTo(OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 0)));
        Assert.That(original.Born.Reason, Is.EqualTo(OwnedItemBirth.Purchased));
    }

    [Test]
    public void AnEndedItemsId_IsNeverReused()
    {
        Guid tape = G(1);
        Buy(tape, "DuctTape", Liam);
        ledger.End(tape, ItemRemovalReason.DestroyedByGame);

        OwnedItemRegistration again = Buy(tape, "DuctTape", Liam);

        Assert.That(again.Status, Is.EqualTo(OwnedItemRegistrationStatus.Reminted));
        Assert.That(ledger.IsLive(tape), Is.False);
    }

    [Test]
    public void AnItemWithNoOwnerOrPrefab_IsRefused()
    {
        Assert.That(Buy(G(1), "Lantern", Guid.Empty).Status, Is.EqualTo(OwnedItemRegistrationStatus.Invalid));
        Assert.That(Buy(G(2), "", Liam).Status, Is.EqualTo(OwnedItemRegistrationStatus.Invalid));
        Assert.That(ledger.Count, Is.Zero);
    }

    [Test]
    public void Essentials_EachGetAClaimSlotOfTheirOwn()
    {
        // The starter kit asks for the slots vanilla uses; a second request for a taken slot gets the
        // highest free one instead of sharing it.
        OwnedItemRegistration radio = Issue(G(1), "CommsRadio", Liam, preferredSlot: 3);
        OwnedItemRegistration map = Issue(G(2), "Map", Liam, preferredSlot: 3);
        OwnedItemRegistration theirs = Issue(G(3), "Map", Bentgear, preferredSlot: 3);

        Assert.That(radio.Entry.ClaimSlot, Is.EqualTo(3));
        Assert.That(map.Entry.ClaimSlot, Is.EqualTo(EssentialClaimSlotPolicy.InventoryCapacity - 1));
        Assert.That(theirs.Entry.ClaimSlot, Is.EqualTo(3), "another owner's claims don't count");
    }

    [Test]
    public void APlainItem_HasNoClaimSlot()
    {
        Assert.That(Buy(G(1), "Lantern", Liam).Entry.ClaimSlot, Is.EqualTo(-1));
    }

    [Test]
    public void PuttingTheMapInTheStolenRadiosSlot_MovesTheRadiosClaimAway()
    {
        // The case EnsureDistinctClaimSlots repairs after the fact today (2026-10-19): with the
        // ledger assigning slots, the clash can't arise.
        Guid radio = Issue(G(1), "CommsRadio", Liam, preferredSlot: 3).Entry.Id;
        Guid map = Issue(G(2), "Map", Liam, preferredSlot: 7).Entry.Id;
        ledger.Move(radio, OwnedItemWhere.With(Bentgear, OwnedItemHold.Hand));

        IReadOnlyList<OwnedItemClaimMove> moves = ledger.SetClaimSlot(map, 3);

        int highest = EssentialClaimSlotPolicy.InventoryCapacity - 1;
        Assert.That(moves.Select(m => (m.Id, m.FromSlot, m.ToSlot)), Is.EqualTo(new[]
        {
            (map, 7, 3),
            (radio, 3, highest)
        }), "the radio's claim goes to the highest free slot, where DV is least likely to have put something");
        Assert.That(ClaimOf(map), Is.EqualTo(3));
        Assert.That(ClaimOf(radio), Is.EqualTo(highest));
    }

    [Test]
    public void ResolvingAnOldSavesClashes_TheItemReallyInTheSlotKeepsIt()
    {
        Guid radio = Issue(G(1), "CommsRadio", Liam, preferredSlot: 3).Entry.Id;
        now++;
        Guid map = Issue(G(2), "Map", Liam, preferredSlot: 9).Entry.Id;
        List<OwnedItemEntry> saved = ledger.All().ToList();
        // A save written before the ledger: both claim slot 3. Bentgear has the radio, and the map is
        // the one really in slot 3.
        saved[0] = new OwnedItemEntry(radio, Liam, "CommsRadio", OwnedItemWhere.With(Bentgear, OwnedItemHold.Hand),
            saved[0].Born, claimSlot: 3);
        saved[1] = new OwnedItemEntry(map, Liam, "Map", OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 3),
            saved[1].Born, claimSlot: 3);
        ledger.Restore(saved, null);

        IReadOnlyList<OwnedItemClaimMove> moves = ledger.ResolveClaimClashes(Liam);

        Assert.That(moves.Select(m => m.Id), Is.EqualTo(new[] { radio }));
        Assert.That(ClaimOf(map), Is.EqualTo(3));
        Assert.That(ClaimOf(radio), Is.EqualTo(EssentialClaimSlotPolicy.InventoryCapacity - 1));
    }

    [Test]
    public void WithEverySlotClaimed_AClashStaysRatherThanDropAClaim()
    {
        OwnedItemLedger small = new(() => Guid.NewGuid(), () => now, claimCapacity: 2);
        small.Register(Essential(G(1), "A", Liam, 0));
        small.Register(Essential(G(2), "B", Liam, 1));
        Guid c = small.Register(Essential(G(3), "C", Liam, 1)).Entry.Id;
        Assert.That(small.TryGet(c, out OwnedItemEntry entry) && entry.ClaimSlot == -1, Is.True,
            "a new essential with no slot left gets no claim rather than a shared one");

        // An old save with three claims on two slots: one of the two on slot 1 has nowhere to go.
        OwnedItemBorn born = new(OwnedItemBirth.Migrated, 1, Guid.Empty);
        small.Restore(new[]
        {
            new OwnedItemEntry(G(1), Liam, "A", OwnedItemWhere.LostAndFound, born, claimSlot: 0),
            new OwnedItemEntry(G(2), Liam, "B", OwnedItemWhere.LostAndFound, born, claimSlot: 1),
            new OwnedItemEntry(G(3), Liam, "C", OwnedItemWhere.LostAndFound, born, claimSlot: 1)
        }, null);

        Assert.That(small.ResolveClaimClashes(Liam), Is.Empty);
        Assert.That(small.All().Select(e => e.ClaimSlot), Is.EqualTo(new[] { 0, 1, 1 }));
    }

    [Test]
    public void Moving_RecordsWhereItIsAndWhereItWas()
    {
        Guid lantern = Buy(G(1), "Lantern", Liam).Entry.Id;
        changes.Clear();

        Assert.That(ledger.Move(lantern, OwnedItemWhere.World("car:L-042")), Is.True);
        Assert.That(ledger.Move(lantern, OwnedItemWhere.World("car:L-042")), Is.True, "no change, no event");

        Assert.That(changes, Has.Count.EqualTo(1));
        Assert.That(changes[0].Kind, Is.EqualTo(OwnedItemChangeKind.Moved));
        Assert.That(changes[0].PreviousWhere, Is.EqualTo(OwnedItemWhere.World()));
        Assert.That(changes[0].Entry.Where, Is.EqualTo(OwnedItemWhere.World("car:L-042")));
    }

    [Test]
    public void AnEndedItem_CantMove_AndLeavesTheOwnersLists()
    {
        Guid lantern = Buy(G(1), "Lantern", Liam).Entry.Id;
        Assert.That(ledger.End(lantern, ItemRemovalReason.DestroyedByGame, detail: "fell with car L-042"), Is.True);

        Assert.That(ledger.Move(lantern, OwnedItemWhere.LostAndFound), Is.False);
        Assert.That(ledger.End(lantern, ItemRemovalReason.DestroyedByGame), Is.False);
        Assert.That(ledger.LiveOwnedBy(Liam), Is.Empty);
        Assert.That(ledger.CountLive(Liam, "Lantern"), Is.Zero);
        Assert.That(ledger.OwnerOf(lantern), Is.EqualTo(Guid.Empty));
        Assert.That(ledger.TryGet(lantern, out OwnedItemEntry ended), Is.True, "kept for lineage");
        Assert.That(ended.Ended.Reason, Is.EqualTo(ItemRemovalReason.DestroyedByGame));
        Assert.That(ended.Ended.Detail, Is.EqualTo("fell with car L-042"));
    }

    [Test]
    public void UsingUpDuctTape_EndsItAndTheEmptyRollIsTheTapeOwners()
    {
        Guid tape = Buy(G(1), "DuctTape", Liam).Entry.Id;

        OwnedItemRegistration roll = ledger.Replace(tape, ItemRemovalReason.DestroyedByGame, new OwnedItemBirthRequest
        {
            Id = G(2),
            Owner = Bentgear,   // whoever was holding it: the roll is still the tape owner's
            Prefab = "DuctTapeEmpty",
            Where = OwnedItemWhere.With(Bentgear, OwnedItemHold.Hand),
            By = Bentgear
        });

        Assert.That(roll.Entry.Owner, Is.EqualTo(Liam));
        Assert.That(roll.Entry.Born.Reason, Is.EqualTo(OwnedItemBirth.Replaced));
        Assert.That(roll.Entry.Born.From, Is.EqualTo(tape));
        Assert.That(ledger.TryGet(tape, out OwnedItemEntry used) && !used.IsLive, Is.True);
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

        Assert.That(ledger.CountLive(Liam, "Lantern", shopStocked: true), Is.EqualTo(2));

        ledger.SetShopStocked(G(1), false);   // a used garage key clears the flag
        ledger.End(G(2), ItemRemovalReason.DestroyedByGame);

        Assert.That(ledger.CountLive(Liam, "Lantern", shopStocked: true), Is.Zero, "both slots are free again");
        Assert.That(ledger.CountLive(Liam, "Lantern"), Is.EqualTo(1), "the unflagged one is still Liam's");
        Assert.That(changes.Select(c => c.Kind), Is.EqualTo(new[] { OwnedItemChangeKind.ShopStocked, OwnedItemChangeKind.Ended }));
    }

    [Test]
    public void WhatAPlayerGetsBackOnJoining_IsEverythingWithThem_OwnedByThemOrNot()
    {
        Guid radio = Buy(G(1), "CommsRadio", Liam).Entry.Id;
        Guid borrowed = Buy(G(2), "Lantern", Bentgear).Entry.Id;
        Guid away = Buy(G(3), "Lighter", Liam).Entry.Id;
        ledger.Move(radio, OwnedItemWhere.With(Liam, OwnedItemHold.Inventory, 0));
        ledger.Move(borrowed, OwnedItemWhere.With(Liam, OwnedItemHold.Hand));
        ledger.Move(away, OwnedItemWhere.LostAndFound);

        Assert.That(ledger.LiveWith(Liam).Select(e => e.Id), Is.EquivalentTo(new[] { radio, borrowed }));
        Assert.That(ledger.LiveOwnedBy(Liam).Select(e => e.Id), Is.EquivalentTo(new[] { radio, away }));
    }

    [Test]
    public void EndedEntries_ArePrunedBeyondAHundredPerOwner_OldestFirst()
    {
        for (int i = 1; i <= OwnedItemLedger.EndedKeptPerOwner + 5; i++)
        {
            now = 1000 + i;
            Buy(G(i), "Banknote", Liam);
            ledger.End(G(i), ItemRemovalReason.MoneyDeposited);
        }
        Guid live = Buy(G(500), "Lantern", Liam).Entry.Id;
        Buy(G(501), "Banknote", Bentgear);
        ledger.End(G(501), ItemRemovalReason.MoneyDeposited);

        Assert.That(ledger.All().Count(e => e.Owner == Liam && !e.IsLive), Is.EqualTo(OwnedItemLedger.EndedKeptPerOwner));
        Assert.That(ledger.TryGet(G(1), out _), Is.False, "the oldest went");
        Assert.That(ledger.TryGet(G(6), out _), Is.True);
        Assert.That(ledger.IsLive(live), Is.True);
        Assert.That(ledger.TryGet(G(501), out _), Is.True, "another owner's history is theirs");
    }

    [Test]
    public void Restoring_KeepsTheFirstOfAnIdSavedTwice_AndRaisesNothing()
    {
        OwnedItemBorn born = new(OwnedItemBirth.Purchased, 5, Liam);
        OwnedItemEntry first = new(G(1), Liam, "Lantern", OwnedItemWhere.LostAndFound, born);
        OwnedItemEntry twice = new(G(1), Bentgear, "Lantern", OwnedItemWhere.World(), born);
        OwnedItemEntry ownerless = new(G(2), Guid.Empty, "Lantern", OwnedItemWhere.World(), born);

        IReadOnlyList<OwnedItemEntry> refused = ledger.Restore(new[] { first, twice, ownerless }, new[] { Liam });

        Assert.That(refused, Is.EqualTo(new[] { twice, ownerless }));
        Assert.That(ledger.OwnerOf(G(1)), Is.EqualTo(Liam));
        Assert.That(ledger.HasStarterKit(Liam), Is.True);
        Assert.That(ledger.HasStarterKit(Bentgear), Is.False);
        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void ForgettingAStarterKit_ClearsTheMarker_Once()
    {
        ledger.MarkStarterKit(Liam);

        Assert.That(ledger.ForgetStarterKit(Liam), Is.True);
        Assert.That(ledger.HasStarterKit(Liam), Is.False);
        Assert.That(ledger.ForgetStarterKit(Liam), Is.False);
    }

    [Test]
    public void EntriesHandedOut_AreCopies()
    {
        Guid lantern = Buy(G(1), "Lantern", Liam).Entry.Id;
        ledger.TryGet(lantern, out OwnedItemEntry copy);

        ledger.Move(lantern, OwnedItemWhere.LostAndFound);

        Assert.That(copy.Where, Is.EqualTo(OwnedItemWhere.World()));
    }

    [Test]
    public void APlace_KeepsOnlyTheFieldsItUses()
    {
        OwnedItemWhere world = OwnedItemWhere.From(OwnedItemPlace.World, Liam, OwnedItemHold.Hand, 4, "car:L-042", 9);
        OwnedItemWhere lost = OwnedItemWhere.From(OwnedItemPlace.LostAndFound, Liam, OwnedItemHold.Hand, 4, "x", 9);

        Assert.That(world, Is.EqualTo(OwnedItemWhere.World("car:L-042")));
        Assert.That(lost, Is.EqualTo(OwnedItemWhere.LostAndFound));
        Assert.That(OwnedItemWhere.From(OwnedItemPlace.Installed, Guid.Empty, OwnedItemHold.None, -1, "car:L-042", 9),
            Is.EqualTo(OwnedItemWhere.Installed("car:L-042", 9)));
    }

    private OwnedItemRegistration Buy(Guid id, string prefab, Guid owner, bool shopStocked = false) =>
        ledger.Register(new OwnedItemBirthRequest
        {
            Id = id,
            Owner = owner,
            Prefab = prefab,
            Where = OwnedItemWhere.World(),
            Reason = OwnedItemBirth.Purchased,
            By = owner,
            ShopStocked = shopStocked
        });

    private OwnedItemRegistration Issue(Guid id, string prefab, Guid owner, int preferredSlot) =>
        ledger.Register(Essential(id, prefab, owner, preferredSlot));

    private static OwnedItemBirthRequest Essential(Guid id, string prefab, Guid owner, int preferredSlot) => new()
    {
        Id = id,
        Owner = owner,
        Prefab = prefab,
        Where = OwnedItemWhere.With(owner, OwnedItemHold.Inventory, preferredSlot),
        Reason = OwnedItemBirth.Issued,
        Essential = true,
        PreferredClaimSlot = preferredSlot
    };

    private int ClaimOf(Guid id) => ledger.TryGet(id, out OwnedItemEntry entry) ? entry.ClaimSlot : -2;

    internal static Guid G(int n) => new($"00000000-0000-0000-0000-{n:x12}");
}
