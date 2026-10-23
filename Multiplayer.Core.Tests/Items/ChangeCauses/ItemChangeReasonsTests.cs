using System;
using System.Linq;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.LiveState;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.ChangeCauses;

[TestFixture]
public sealed class ItemChangeReasonsTests
{
    private const byte Host = 1, Bentgear = 3, Liam = 4;

    private static ItemWhereabouts At(AuthorityPlacement placement, byte holder = 0, byte owner = 0) =>
        new(placement, holder, owner);

    [TestCase(AuthorityPlacement.World, AuthorityPlacement.PlayerHand, false, ItemMoveReason.PickedUp)]
    [TestCase(AuthorityPlacement.TrainInterior, AuthorityPlacement.PlayerHand, false, ItemMoveReason.PickedUp)]
    [TestCase(AuthorityPlacement.Attached, AuthorityPlacement.PlayerHand, false, ItemMoveReason.PickedUp)]
    [TestCase(AuthorityPlacement.PlayerInventory, AuthorityPlacement.PlayerHand, false, ItemMoveReason.TakenFromInventory)]
    [TestCase(AuthorityPlacement.PlayerHand, AuthorityPlacement.PlayerInventory, false, ItemMoveReason.StoredInInventory)]
    [TestCase(AuthorityPlacement.World, AuthorityPlacement.PlayerInventory, false, ItemMoveReason.StoredInInventory)]
    [TestCase(AuthorityPlacement.PlayerHand, AuthorityPlacement.World, false, ItemMoveReason.Dropped)]
    [TestCase(AuthorityPlacement.PlayerHand, AuthorityPlacement.World, true, ItemMoveReason.Thrown)]
    [TestCase(AuthorityPlacement.PlayerInventory, AuthorityPlacement.TrainInterior, false, ItemMoveReason.Dropped)]
    [TestCase(AuthorityPlacement.PlayerHand, AuthorityPlacement.Attached, false, ItemMoveReason.Attached)]
    [TestCase(AuthorityPlacement.Attached, AuthorityPlacement.World, false, ItemMoveReason.Detached)]
    [TestCase(AuthorityPlacement.World, AuthorityPlacement.TrainInterior, false, ItemMoveReason.Moved)]
    [TestCase(AuthorityPlacement.PlayerHand, AuthorityPlacement.Installed, false, ItemMoveReason.InstalledAsGadget)]
    [TestCase(AuthorityPlacement.World, AuthorityPlacement.Installed, false, ItemMoveReason.InstalledAsGadget)]
    [TestCase(AuthorityPlacement.Installed, AuthorityPlacement.World, false, ItemMoveReason.RemovedAsGadget)]
    [TestCase(AuthorityPlacement.Installed, AuthorityPlacement.PlayerHand, false, ItemMoveReason.RemovedAsGadget)]
    public void Classify_NamesTheMoveByWhereItWentFrom(AuthorityPlacement from, AuthorityPlacement to, bool thrown,
        ItemMoveReason expected)
    {
        Assert.That(ItemMoveClassifier.Classify(At(from, Bentgear), At(to, Bentgear), thrown), Is.EqualTo(expected));
    }

    [TestCase(AuthorityPlacement.PlayerHand)]
    [TestCase(AuthorityPlacement.PlayerInventory)]
    public void Classify_AnotherPlayersItem_IsTakenFromThem(AuthorityPlacement to)
    {
        // The radio in Bentgear's inventory turning up in Liam's hand is a theft, not a pickup.
        Assert.That(ItemMoveClassifier.Classify(At(AuthorityPlacement.PlayerInventory, Bentgear, Bentgear),
            At(to, Liam, Bentgear), false), Is.EqualTo(ItemMoveReason.TakenFromPlayer));
    }

    [Test]
    public void Classify_NothingButAHandSwap_IsUnknownAndUnchanged()
    {
        ItemWhereabouts before = At(AuthorityPlacement.PlayerHand, Bentgear, Bentgear);
        ItemWhereabouts after = At(AuthorityPlacement.PlayerHand, Bentgear, Bentgear);

        Assert.That(ItemMoveClassifier.Changed(before, after), Is.False);
        Assert.That(ItemMoveClassifier.Classify(before, after, false), Is.EqualTo(ItemMoveReason.Unknown));
    }

    [Test]
    public void Changed_CountsAnOwnerChangeAlone()
    {
        Assert.That(ItemMoveClassifier.Changed(At(AuthorityPlacement.World), At(AuthorityPlacement.World, owner: Liam)),
            Is.True);
    }

    [Test]
    public void Whereabouts_IgnoreAHolderTheItemIsNoLongerWith()
    {
        // A dropped item's record can keep the old holder id; it isn't held by them.
        Assert.That(At(AuthorityPlacement.World, Bentgear).HolderPlayerId, Is.EqualTo(0));
        Assert.That(ItemMoveClassifier.Changed(At(AuthorityPlacement.World, Bentgear), At(AuthorityPlacement.World)),
            Is.False);
    }

    [Test]
    public void Describe_SaysWhereFromAndToAndTheNewOwner()
    {
        string Names(byte id) => id switch { Bentgear => "Bentgear29", Liam => "Liam", _ => null };

        Assert.That(ItemMoveClassifier.Describe(At(AuthorityPlacement.World), At(AuthorityPlacement.PlayerHand, Liam, Liam), Names),
            Is.EqualTo("world -> Liam's hand, owner none -> Liam"));
        Assert.That(ItemMoveClassifier.Describe(At(AuthorityPlacement.PlayerHand, Host, Bentgear),
                At(AuthorityPlacement.TrainInterior, 0, Bentgear), Names),
            Is.EqualTo("P1's hand -> train car"));
        Assert.That(ItemMoveClassifier.Describe(At(AuthorityPlacement.World, owner: Liam), At(AuthorityPlacement.World), Names),
            Is.EqualTo("owner Liam -> none"));
    }

    [Test]
    public void LogFormat_IsOneLineWithNameNetIdAndCause()
    {
        var removed = new ChangeCause<ItemRemovalReason>(ItemRemovalReason.JobDocumentDestroyed,
            detail: "job CME-SU-12 Completed");
        var moved = new ChangeCause<ItemMoveReason>(ItemMoveReason.PickedUp, Bentgear, "Bentgear29",
            detail: "world -> Bentgear29's hand");

        Assert.That(ItemChangeLogFormat.Removed("JobOverview(Clone)", 801, removed),
            Is.EqualTo("Item JobOverview (netId 801) removed: JobDocumentDestroyed (job CME-SU-12 Completed)"));
        Assert.That(ItemChangeLogFormat.Moved("ItemRadio", 763, moved),
            Is.EqualTo("Item ItemRadio (netId 763) moved: PickedUp by Bentgear29 (world -> Bentgear29's hand)"));
        Assert.That(ItemChangeLogFormat.ItemName(null), Is.EqualTo("?"));
    }

    [Test]
    public void RecentRemovals_FindsTheNewestRemovalOfAReusedNetId()
    {
        var removals = new RecentItemRemovals(4);
        removals.Add(Removal(801, "JobOverview", ItemRemovalReason.JobDocumentDestroyed));
        removals.Add(Removal(763, "ItemRadio", ItemRemovalReason.CollectedToLostAndFound));
        removals.Add(Removal(801, "Scanner", ItemRemovalReason.DestroyedByGame));

        Assert.That(removals.TryGetLatest(801, out ItemRemovalRecord latest), Is.True);
        Assert.That(latest.ItemName, Is.EqualTo("Scanner"));
        Assert.That(latest.Cause.Reason, Is.EqualTo(ItemRemovalReason.DestroyedByGame));
        Assert.That(removals.TryGetLatest(5, out _), Is.False);
    }

    [Test]
    public void RecentRemovals_KeepsOnlyTheNewestUpToCapacity_OldestFirst()
    {
        var removals = new RecentItemRemovals(3);
        for (ushort id = 1; id <= 5; id++)
            removals.Add(Removal(id, "Item" + id, ItemRemovalReason.Unknown));

        Assert.That(removals.Count, Is.EqualTo(3));
        Assert.That(removals.Snapshot().Select(record => record.NetId), Is.EqualTo(new ushort[] { 3, 4, 5 }));
        Assert.That(removals.TryGetLatest(2, out _), Is.False);

        removals.Clear();
        Assert.That(removals.Count, Is.EqualTo(0));
        Assert.That(removals.Snapshot(), Is.Empty);
    }

    [Test]
    public void RecentRemovals_RejectsNoCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentItemRemovals(0));
    }

    [Test]
    public void ReasonsAreByteSizedWithUnknownFirst()
    {
        // On the wire as one byte each (ChangeCauseData); Unknown is the fallback for a default cause.
        Assert.That(Enum.GetUnderlyingType(typeof(ItemRemovalReason)), Is.EqualTo(typeof(byte)));
        Assert.That(Enum.GetUnderlyingType(typeof(ItemMoveReason)), Is.EqualTo(typeof(byte)));
        Assert.That((byte)ItemRemovalReason.Unknown, Is.EqualTo(0));
        Assert.That((byte)ItemMoveReason.Unknown, Is.EqualTo(0));
    }

    private static ItemRemovalRecord Removal(ushort netId, string name, ItemRemovalReason reason) =>
        new(netId, name, At(AuthorityPlacement.World), new ChangeCause<ItemRemovalReason>(reason), default);
}
