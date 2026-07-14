using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class StorageTransitionPlannerTests
{
    private static readonly StorageMembership[] SingleTargets =
    {
        StorageMembership.Inventory,
        StorageMembership.World,
        StorageMembership.LostAndFound,
        StorageMembership.ItemContainer
    };

    [Test]
    public void EveryMembershipCombination_NormalizesToExactlyWorld()
    {
        for (int raw = 0; raw < 16; raw++)
        {
            StorageMembership current = (StorageMembership)raw;
            StorageTransitionPlan plan = StorageTransitionPlanner.Plan(
                new FakeStorage(current), StorageMembership.World);
            StorageMembership final = (current & ~plan.Remove) | plan.Add;
            Assert.That(plan.Accepted, Is.True, current.ToString());
            Assert.That(final, Is.EqualTo(StorageMembership.World), current.ToString());
        }
    }

    [TestCaseSource(nameof(SingleTargets))]
    public void EverySingleTarget_ProducesExactlyOneMembership(StorageMembership target)
    {
        StorageMembership all = StorageMembership.Inventory | StorageMembership.World |
            StorageMembership.LostAndFound | StorageMembership.ItemContainer;
        StorageTransitionPlan plan = StorageTransitionPlanner.Plan(new FakeStorage(all), target);
        StorageMembership final = (all & ~plan.Remove) | plan.Add;
        Assert.That(final, Is.EqualTo(target));
        Assert.That(plan.RepairedMultipleMembership, Is.True);
    }

    [Test]
    public void AlreadyInTarget_IsNoOp()
    {
        StorageTransitionPlan plan = StorageTransitionPlanner.Plan(
            new FakeStorage(StorageMembership.World), StorageMembership.World);
        Assert.That(plan.Remove, Is.EqualTo(StorageMembership.None));
        Assert.That(plan.Add, Is.EqualTo(StorageMembership.None));
        Assert.That(plan.RepairedMultipleMembership, Is.False);
    }

    [Test]
    public void NoStorageTarget_RemovesAllMemberships()
    {
        StorageMembership current = StorageMembership.World | StorageMembership.Inventory;
        StorageTransitionPlan plan = StorageTransitionPlanner.Plan(
            new FakeStorage(current), StorageMembership.None);
        Assert.That(plan.Remove, Is.EqualTo(current));
        Assert.That(plan.Add, Is.EqualTo(StorageMembership.None));
    }

    [Test]
    public void UnavailableStorageAndMultiTargetAreRejected()
    {
        Assert.That(StorageTransitionPlanner.Plan(new FakeStorage(StorageMembership.None, false),
            StorageMembership.World).Reason, Is.EqualTo("storage-unavailable"));
        Assert.That(StorageTransitionPlanner.Plan(new FakeStorage(StorageMembership.None),
            StorageMembership.World | StorageMembership.Inventory).Reason,
            Is.EqualTo("invalid-storage-target"));
    }

    private sealed class FakeStorage : IStorageView
    {
        public FakeStorage(StorageMembership membership, bool available = true)
        {
            Membership = membership;
            IsAvailable = available;
        }
        public bool IsAvailable { get; }
        public StorageMembership Membership { get; }
    }
}
