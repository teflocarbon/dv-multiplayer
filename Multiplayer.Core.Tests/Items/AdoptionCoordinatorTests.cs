using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class AdoptionCoordinatorTests
{
    [Test]
    public void Client_AllowsOnePendingTokenPerItemAndOneItemPerToken()
    {
        AdoptionCoordinator<object> coordinator = new();
        object first = new();
        object second = new();

        Assert.That(coordinator.TryBegin(first, "token-a"), Is.EqualTo(AdoptionBeginStatus.Started));
        Assert.That(coordinator.TryBegin(first, "token-b"), Is.EqualTo(AdoptionBeginStatus.ItemAlreadyPending));
        Assert.That(coordinator.TryBegin(second, "token-a"), Is.EqualTo(AdoptionBeginStatus.TokenAlreadyPending));
        Assert.That(coordinator.PendingCount, Is.EqualTo(1));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void Client_RejectsInvalidToken(string token)
    {
        Assert.That(new AdoptionCoordinator<object>().TryBegin(new object(), token),
            Is.EqualTo(AdoptionBeginStatus.InvalidToken));
    }

    [Test]
    public void Client_AcceptedResultReturnsOriginalObjectExactlyOnce()
    {
        AdoptionCoordinator<object> coordinator = new();
        object item = new();
        coordinator.TryBegin(item, "token");

        AdoptionResolution<object> first = coordinator.Resolve("token", true, 772);
        AdoptionResolution<object> duplicate = coordinator.Resolve("token", true, 772);

        Assert.That(first.Status, Is.EqualTo(AdoptionResolutionStatus.Accepted));
        Assert.That(first.Item, Is.SameAs(item));
        Assert.That(first.AssignedNetId, Is.EqualTo(772));
        Assert.That(duplicate.Status, Is.EqualTo(AdoptionResolutionStatus.AlreadyCompleted));
        Assert.That(coordinator.PendingCount, Is.Zero);
    }

    [Test]
    public void Client_RejectionAndInvalidAcceptedIdReleasePendingItem()
    {
        AdoptionCoordinator<object> coordinator = new();
        object rejectedItem = new();
        object invalidItem = new();
        coordinator.TryBegin(rejectedItem, "reject");
        coordinator.TryBegin(invalidItem, "invalid");

        Assert.That(coordinator.Resolve("reject", false, 0).Status,
            Is.EqualTo(AdoptionResolutionStatus.Rejected));
        Assert.That(coordinator.Resolve("invalid", true, 0).Status,
            Is.EqualTo(AdoptionResolutionStatus.InvalidAcceptedResult));
        Assert.That(coordinator.PendingCount, Is.Zero);
    }

    [Test]
    public void Client_UnknownResultDoesNotConsumeOtherPendingRequest()
    {
        AdoptionCoordinator<object> coordinator = new();
        coordinator.TryBegin(new object(), "known");
        Assert.That(coordinator.Resolve("unknown", true, 1).Status,
            Is.EqualTo(AdoptionResolutionStatus.UnknownToken));
        Assert.That(coordinator.PendingCount, Is.EqualTo(1));
    }

    [Test]
    public void Host_TokenIsScopedPerAuthenticatedPlayer()
    {
        HostAdoptionRegistry registry = new();
        Assert.That(registry.TryBegin(1, "same"), Is.EqualTo(HostAdoptionBeginStatus.Started));
        Assert.That(registry.TryBegin(2, "same"), Is.EqualTo(HostAdoptionBeginStatus.Started));
    }

    [Test]
    public void Host_DuplicatePendingTokenCannotCreateSecondItem()
    {
        HostAdoptionRegistry registry = new();
        Assert.That(registry.TryBegin(1, "token"), Is.EqualTo(HostAdoptionBeginStatus.Started));
        Assert.That(registry.TryBegin(1, "token"), Is.EqualTo(HostAdoptionBeginStatus.AlreadyPending));
    }

    [Test]
    public void Host_CompletedTokenReplaysSameCanonicalMapping()
    {
        HostAdoptionRegistry registry = new();
        registry.TryBegin(1, "token");
        registry.Complete(1, "token", true, 772, string.Empty);

        Assert.That(registry.TryBegin(1, "token"), Is.EqualTo(HostAdoptionBeginStatus.AlreadyCompleted));
        Assert.That(registry.TryGetCompleted(1, "token", out HostAdoptionOutcome outcome), Is.True);
        Assert.That(outcome.Accepted, Is.True);
        Assert.That(outcome.AssignedNetId, Is.EqualTo(772));
    }

    [Test]
    public void Host_RejectedTokenReplaysStableRejection()
    {
        HostAdoptionRegistry registry = new();
        registry.TryBegin(1, "token");
        registry.Complete(1, "token", false, 0, "unknown-prefab");
        registry.TryGetCompleted(1, "token", out HostAdoptionOutcome outcome);
        Assert.That(outcome.Accepted, Is.False);
        Assert.That(outcome.RejectionReason, Is.EqualTo("unknown-prefab"));
    }
}
