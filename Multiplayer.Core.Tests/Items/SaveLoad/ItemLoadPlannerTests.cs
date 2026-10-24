using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Core.Items.SaveLoad;
using Multiplayer.Core.Tests.Items.Ledger;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Tests.Items.SaveLoad;

[TestFixture]
public sealed class ItemLoadPlannerTests
{
    private static readonly Guid Liam = ItemLedgerTests.G(0xA1);
    private static readonly Guid Bentgear = ItemLedgerTests.G(0xB2);
    private static readonly Guid Lantern = ItemLedgerTests.G(1);
    private const string Car = "car-guid-1";

    private static readonly LoadWorld Everything = new();

    [Test]
    public void AnInventoryItem_StaysDataUntilItsHolderJoins()
    {
        LoadStep step = Only(Entry(Home.InSlot(Liam, 4)), true, Everything);

        Assert.That(step.Action, Is.EqualTo(LoadAction.KeepCold));
        Assert.That(step.Home, Is.EqualTo(Home.InSlot(Liam, 4)));
    }

    [Test]
    public void AnItemSavedWithABorrower_GoesToItsOwnersLostAndFound()
    {
        LoadStep step = Only(Entry(Home.InSlot(Bentgear, 4)), true, Everything);

        Assert.That(step.Action, Is.EqualTo(LoadAction.Recover));
        Assert.That(step.Home, Is.EqualTo(Home.LostAndFound));
        Assert.That(step.Reason, Is.EqualTo(LostItemReason.SaveRecovery));
    }

    [Test]
    public void LooseItems_AreMadeLiveInTheirHomes()
    {
        foreach (Home home in new[] { Home.World, Home.Shelf("shelf"), Home.Snapped("hook"), Home.TrainInterior(Car), Home.Attached(Car, true) })
            Assert.That(Only(Entry(home), true, Everything).Action, Is.EqualTo(LoadAction.MakeLive), home.ToString());
    }

    [Test]
    public void ATrainItemWhoseCarIsGone_IsRecovered()
    {
        LoadWorld noCars = new() { CarExists = _ => false };

        foreach (Home home in new[] { Home.TrainInterior(Car), Home.Attached(Car, false) })
        {
            LoadStep step = Only(Entry(home), true, noCars);
            Assert.That(step.Action, Is.EqualTo(LoadAction.Recover), home.ToString());
            Assert.That(step.Reason, Is.EqualTo(LostItemReason.SaveRecovery));
        }
    }

    [Test]
    public void AnInstalledGadgetWhoseCarIsGone_GoesToLostAndFound()
    {
        LoadStep step = Only(Entry(Home.Installed(Car)), false, new LoadWorld { CarExists = _ => false });

        Assert.That(step.Action, Is.EqualTo(LoadAction.Recover));
        Assert.That(step.Reason, Is.EqualTo(LostItemReason.GadgetDestinationMissing));
    }

    [Test]
    public void AnInstalledGadget_IsVanillasToMake_AndTheWorldIsNeverGone()
    {
        LoadWorld noCars = new() { CarExists = _ => false };

        Assert.That(Only(Entry(Home.Installed(Car)), false, Everything).Action, Is.EqualTo(LoadAction.LeaveInstalled));
        Assert.That(Only(Entry(Home.Installed(Home.WorldAnchor)), false, noCars).Action, Is.EqualTo(LoadAction.LeaveInstalled));
    }

    [Test]
    public void AnItemWithNoSavedRow_IsMadeFreshInLostAndFound()
    {
        LoadStep step = Only(Entry(Home.World), false, Everything);

        Assert.That(step.Action, Is.EqualTo(LoadAction.Recover));
        Assert.That(step.Reason, Is.EqualTo(LostItemReason.SaveRecovery));
    }

    [Test]
    public void AnItemWhosePrefabIsGone_IsUnloadable_WhereverItWas()
    {
        LoadWorld noPrefab = new() { PrefabExists = _ => false };

        foreach (Home home in new[] { Home.InSlot(Liam, 2), Home.World, Home.LostAndFound, Home.Container(Guid.NewGuid(), 1) })
            Assert.That(Only(Entry(home), true, noPrefab).Action, Is.EqualTo(LoadAction.Unloadable), home.ToString());
    }

    [Test]
    public void ContainerAndLostAndFoundItems_GoBackThere()
    {
        Guid box = Guid.NewGuid();

        Assert.That(Only(Entry(Home.Container(box, 3)), true, Everything).Action, Is.EqualTo(LoadAction.IntoContainer));
        Assert.That(Only(Entry(Home.LostAndFound), true, Everything).Action, Is.EqualTo(LoadAction.IntoLostAndFound));
        // With its row gone it's made fresh there: already where a recovery would send it.
        Assert.That(Only(Entry(Home.LostAndFound), false, Everything).Action, Is.EqualTo(LoadAction.IntoLostAndFound));
    }

    [Test]
    public void EndedEntries_AreNotPlanned()
    {
        LedgerEntry ended = new(Lantern, OwnerRef.Player(Liam), "Lantern", Home.World, Born(),
            ended: new End(ItemRemovalReason.Unknown, 1));

        Assert.That(ItemLoadPlanner.Plan(new[] { ended }, new HashSet<Guid> { Lantern }, Everything), Is.Empty);
    }

    private static LoadStep Only(LedgerEntry entry, bool hasRow, LoadWorld world)
    {
        IReadOnlyList<LoadStep> steps = ItemLoadPlanner.Plan(new[] { entry },
            hasRow ? new HashSet<Guid> { entry.Id } : new HashSet<Guid>(), world);
        Assert.That(steps, Has.Count.EqualTo(1));
        return steps.Single();
    }

    private static LedgerEntry Entry(Home home) =>
        new(Lantern, OwnerRef.Player(Liam), "Lantern", home, Born());

    private static Birth Born() => new(BirthReason.Purchased, 1, Liam);
}
