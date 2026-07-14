using Multiplayer.Core.Replication;
using NUnit.Framework;
using System;

namespace Multiplayer.Core.Tests.Replication;

[TestFixture]
public sealed class RecipientCompletionTests
{
    private static readonly DateTime Start = new(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ExpectedRecipients_AllApplied_Completes()
    {
        RecipientCompletionResult result = Evaluate(new[]
        {
            Recipient(1, sent: true, received: true, applied: true),
            Recipient(2, sent: true, received: true, applied: true)
        }, 10);
        Assert.That(result.Status, Is.EqualTo(ReplicationCompletionStatus.Complete));
    }

    [Test]
    public void AnyPendingRecipient_KeepsOperationPendingBeforeTimeout()
    {
        RecipientCompletionResult result = Evaluate(new[]
        {
            Recipient(1, true, true, true),
            Recipient(2, true, false, false)
        }, 749);
        Assert.That(result.Status, Is.EqualTo(ReplicationCompletionStatus.Pending));
    }

    [TestCase(749, ReplicationCompletionStatus.Pending)]
    [TestCase(750, ReplicationCompletionStatus.Discontinuity)]
    public void SentNotReceived_UsesExactTimeoutBoundary(int age,
        ReplicationCompletionStatus expected)
    {
        RecipientCompletionResult result = Evaluate(new[] { Recipient(2, true, false, false) }, age);
        Assert.That(result.Status, Is.EqualTo(expected));
        if (expected == ReplicationCompletionStatus.Discontinuity)
            Assert.That(result.Reason, Is.EqualTo("P2:sent-not-received"));
    }

    [TestCase(1999, ReplicationCompletionStatus.Pending)]
    [TestCase(2000, ReplicationCompletionStatus.Discontinuity)]
    public void ReceivedNotApplied_UsesExactTimeoutBoundary(int age,
        ReplicationCompletionStatus expected)
    {
        RecipientCompletionResult result = Evaluate(new[] { Recipient(2, true, true, false) }, age);
        Assert.That(result.Status, Is.EqualTo(expected));
        if (expected == ReplicationCompletionStatus.Discontinuity)
            Assert.That(result.Reason, Is.EqualTo("P2:received-not-applied"));
    }

    [Test]
    public void AppliedRecipient_RecoversFromPriorTimeoutState()
    {
        RecipientProgress recipient = Recipient(2, true, false, false);
        Assert.That(Evaluate(new[] { recipient }, 800).Status,
            Is.EqualTo(ReplicationCompletionStatus.Discontinuity));
        recipient.Received = true;
        recipient.Applied = true;
        RecipientCompletionResult recovered = Evaluate(new[] { recipient }, 900);
        Assert.That(recovered.Status, Is.EqualTo(ReplicationCompletionStatus.Complete));
        Assert.That(recovered.RecipientDiscontinuities, Is.Empty);
    }

    [TestCase(false, false, ReplicationCompletionStatus.Pending)]
    [TestCase(true, false, ReplicationCompletionStatus.Pending)]
    [TestCase(false, true, ReplicationCompletionStatus.Pending)]
    [TestCase(true, true, ReplicationCompletionStatus.Complete)]
    public void ZeroRecipientOperation_RequiresHostApplyAndRelay(bool hostApplied,
        bool relayFinished, ReplicationCompletionStatus expected)
    {
        RecipientCompletionResult result = RecipientCompletionEvaluator.Evaluate(
            new RecipientCompletionInput
            {
                LastUpdatedUtc = Start,
                HostApplied = hostApplied,
                RelayFinished = relayFinished,
                Recipients = Array.Empty<RecipientProgress>()
            }, Start);
        Assert.That(result.Status, Is.EqualTo(expected));
    }

    [Test]
    public void ValidationRejection_OverridesRecipientsAndTimeouts()
    {
        RecipientCompletionResult result = RecipientCompletionEvaluator.Evaluate(
            new RecipientCompletionInput
            {
                LastUpdatedUtc = Start,
                ValidationRejected = true,
                Recipients = new[] { Recipient(2, true, false, false) }
            }, Start.AddMinutes(1));
        Assert.That(result.Status, Is.EqualTo(ReplicationCompletionStatus.Rejected));
        Assert.That(result.Reason, Is.EqualTo("validation-rejected"));
    }

    [Test]
    public void MultipleFailures_ChooseStableLowestPlayerReason()
    {
        RecipientCompletionResult result = Evaluate(new[]
        {
            Recipient(3, true, false, false),
            Recipient(1, true, true, false),
            Recipient(2, true, false, false)
        }, 2500);
        Assert.That(result.Status, Is.EqualTo(ReplicationCompletionStatus.Discontinuity));
        Assert.That(result.Reason, Is.EqualTo("P1:received-not-applied"));
        Assert.That(result.RecipientDiscontinuities.Count, Is.EqualTo(3));
    }

    private static RecipientCompletionResult Evaluate(RecipientProgress[] recipients, int ageMs) =>
        RecipientCompletionEvaluator.Evaluate(new RecipientCompletionInput
        {
            LastUpdatedUtc = Start,
            Recipients = recipients
        }, Start.AddMilliseconds(ageMs));

    private static RecipientProgress Recipient(byte id, bool sent, bool received, bool applied) =>
        new() { PlayerId = id, Sent = sent, Received = received, Applied = applied };
}
