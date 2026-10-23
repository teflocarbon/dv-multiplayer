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
    private static readonly Guid Host = new(1, 0, 0, new byte[8]), Bentgear = new(3, 0, 0, new byte[8]),
        Liam = new(4, 0, 0, new byte[8]);

    private static ItemWhereabouts At(LivePlacement placement, Guid holder = default, Guid owner = default) =>
        new(placement, holder, owner);

    [TestCase(LivePlacement.World, LivePlacement.PlayerHand, false, ItemMoveReason.PickedUp)]
    [TestCase(LivePlacement.TrainInterior, LivePlacement.PlayerHand, false, ItemMoveReason.PickedUp)]
    [TestCase(LivePlacement.Attached, LivePlacement.PlayerHand, false, ItemMoveReason.PickedUp)]
    [TestCase(LivePlacement.PlayerInventory, LivePlacement.PlayerHand, false, ItemMoveReason.TakenFromInventory)]
    [TestCase(LivePlacement.PlayerHand, LivePlacement.PlayerInventory, false, ItemMoveReason.StoredInInventory)]
    [TestCase(LivePlacement.World, LivePlacement.PlayerInventory, false, ItemMoveReason.StoredInInventory)]
    [TestCase(LivePlacement.PlayerHand, LivePlacement.World, false, ItemMoveReason.Dropped)]
    [TestCase(LivePlacement.PlayerHand, LivePlacement.World, true, ItemMoveReason.Thrown)]
    [TestCase(LivePlacement.PlayerInventory, LivePlacement.TrainInterior, false, ItemMoveReason.Dropped)]
    [TestCase(LivePlacement.PlayerHand, LivePlacement.Attached, false, ItemMoveReason.Attached)]
    [TestCase(LivePlacement.Attached, LivePlacement.World, false, ItemMoveReason.Detached)]
    [TestCase(LivePlacement.World, LivePlacement.TrainInterior, false, ItemMoveReason.Moved)]
    [TestCase(LivePlacement.PlayerHand, LivePlacement.Installed, false, ItemMoveReason.InstalledAsGadget)]
    [TestCase(LivePlacement.World, LivePlacement.Installed, false, ItemMoveReason.InstalledAsGadget)]
    [TestCase(LivePlacement.Installed, LivePlacement.World, false, ItemMoveReason.RemovedAsGadget)]
    [TestCase(LivePlacement.Installed, LivePlacement.PlayerHand, false, ItemMoveReason.RemovedAsGadget)]
    public void Classify_NamesTheMoveByWhereItWentFrom(LivePlacement from, LivePlacement to, bool thrown,
        ItemMoveReason expected)
    {
        Assert.That(ItemMoveClassifier.Classify(At(from, Bentgear), At(to, Bentgear), thrown), Is.EqualTo(expected));
    }

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    public void Classify_AnotherPlayersItem_IsTakenFromThem(LivePlacement to)
    {
        // The radio in Bentgear's inventory turning up in Liam's hand is a theft, not a pickup.
        Assert.That(ItemMoveClassifier.Classify(At(LivePlacement.PlayerInventory, Bentgear, Bentgear),
            At(to, Liam, Bentgear), false), Is.EqualTo(ItemMoveReason.TakenFromPlayer));
    }

    [Test]
    public void Classify_NothingButAHandSwap_IsUnknownAndUnchanged()
    {
        ItemWhereabouts before = At(LivePlacement.PlayerHand, Bentgear, Bentgear);
        ItemWhereabouts after = At(LivePlacement.PlayerHand, Bentgear, Bentgear);

        Assert.That(ItemMoveClassifier.Changed(before, after), Is.False);
        Assert.That(ItemMoveClassifier.Classify(before, after, false), Is.EqualTo(ItemMoveReason.Unknown));
    }

    [Test]
    public void Changed_CountsAnOwnerChangeAlone()
    {
        Assert.That(ItemMoveClassifier.Changed(At(LivePlacement.World), At(LivePlacement.World, owner: Liam)),
            Is.True);
    }

    [Test]
    public void Whereabouts_IgnoreAHolderTheItemIsNoLongerWith()
    {
        // A dropped item's record can keep the old holder id; it isn't held by them.
        Assert.That(At(LivePlacement.World, Bentgear).Holder, Is.EqualTo(Guid.Empty));
        Assert.That(ItemMoveClassifier.Changed(At(LivePlacement.World, Bentgear), At(LivePlacement.World)),
            Is.False);
    }

    [Test]
    public void Describe_SaysWhereFromAndToAndTheNewOwner()
    {
        string Names(Guid id) => id == Bentgear ? "Bentgear29" : id == Liam ? "Liam" : null;

        Assert.That(ItemMoveClassifier.Describe(At(LivePlacement.World), At(LivePlacement.PlayerHand, Liam, Liam), Names),
            Is.EqualTo("world -> Liam's hand, owner none -> Liam"));
        Assert.That(ItemMoveClassifier.Describe(At(LivePlacement.PlayerHand, Host, Bentgear),
                At(LivePlacement.TrainInterior, Guid.Empty, Bentgear), Names),
            Is.EqualTo("00000001's hand -> train car"));
        Assert.That(ItemMoveClassifier.Describe(At(LivePlacement.World, owner: Liam), At(LivePlacement.World), Names),
            Is.EqualTo("owner Liam -> none"));
    }

    [Test]
    public void LogFormat_IsOneLineWithNameNetIdAndCause()
    {
        var removed = new ChangeCause<ItemRemovalReason>(ItemRemovalReason.JobDocumentDestroyed,
            detail: "job CME-SU-12 Completed");
        var moved = new ChangeCause<ItemMoveReason>(ItemMoveReason.PickedUp, 3, "Bentgear29",
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
        new(netId, name, At(LivePlacement.World), new ChangeCause<ItemRemovalReason>(reason), default);
}
