using Multiplayer.Core.Items.Projection;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Projection;

[TestFixture]
public sealed class ProjectionAcksTests
{
    [Test]
    public void AProjectionSentIsAcknowledged() =>
        Assert.That(ProjectionAcks.Apply(ProjectionAck.Projected, true, true, true),
            Is.EqualTo(ProjectionAckOutcome.Acknowledged));

    [TestCase(false, true, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    public void OneTheServerDidntSendIsRejected(bool exists, bool revisionReached, bool knows) =>
        Assert.That(ProjectionAcks.Apply(ProjectionAck.Projected, exists, revisionReached, knows),
            Is.EqualTo(ProjectionAckOutcome.Rejected));

    [TestCase(1, 5f)]
    [TestCase(2, 10f)]
    [TestCase(3, 20f)]
    [TestCase(4, 40f)]
    public void EachFailureInARowWaitsTwiceAsLong(int failures, float seconds) =>
        Assert.That(ProjectionRetry.DelayAfter(failures), Is.EqualTo(seconds));

    [Test]
    public void AfterTheLastRetryItIsntSentAgainWhileInInterest()
    {
        // B2: a prefab the client lacks fails every time; it was sent every 5 s for ever.
        Assert.That(ProjectionRetry.DelayAfter(ProjectionRetry.MaxRetries + 1), Is.EqualTo(float.PositiveInfinity));
        Assert.That(ProjectionRetry.DelayAfter(0), Is.EqualTo(0f), "no failure yet");
    }

    [Test]
    public void AFailedCreateIsForgottenSoItIsSentAgain() =>
        Assert.That(ProjectionAcks.Apply(ProjectionAck.Failed, true, true, true),
            Is.EqualTo(ProjectionAckOutcome.Forgotten));

    [Test]
    public void ALateRetirementDoesntHoldARe_EntryBack() =>
        // F45: retired, back in interest and its Create sent, and then the retirement's ack arrives.
        Assert.That(ProjectionAcks.Apply(ProjectionAck.Retired, true, true, true),
            Is.EqualTo(ProjectionAckOutcome.Unacknowledged));

    [Test]
    public void ARetirementOrAFailureOfAnItemGoneIsOnlyUnacknowledged()
    {
        Assert.That(ProjectionAcks.Apply(ProjectionAck.Retired, false, false, false), Is.EqualTo(ProjectionAckOutcome.Unacknowledged));
        Assert.That(ProjectionAcks.Apply(ProjectionAck.Failed, false, false, false), Is.EqualTo(ProjectionAckOutcome.Unacknowledged));
        Assert.That(ProjectionAcks.Apply(ProjectionAck.Failed, true, true, false), Is.EqualTo(ProjectionAckOutcome.Unacknowledged), "already forgotten");
    }
}
