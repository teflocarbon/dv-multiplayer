using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class TrainBaselineReceiptTests
{
    [Test]
    public void CompleteRequiresEverySequenceAndExactCarCount()
    {
        TrainBaselineReceipt receipt = new();
        Assert.That(receipt.Begin(7, 2, 9, string.Empty), Is.True);
        Assert.That(receipt.Admit(7, 0, out _), Is.EqualTo(TrainBaselineDisposition.Apply));
        Assert.That(receipt.Commit(0, 1), Is.True);
        Assert.That(receipt.TryComplete(7, 2, 9), Is.False);
        Assert.That(receipt.Failure, Does.Contain("incomplete baseline"));
    }

    [Test]
    public void DuplicateSequenceIsIdempotent()
    {
        TrainBaselineReceipt receipt = new();
        Assert.That(receipt.Begin(8, 1, 3, string.Empty), Is.True);
        Assert.That(receipt.Admit(8, 0, out _), Is.EqualTo(TrainBaselineDisposition.Apply));
        Assert.That(receipt.Commit(0, 3), Is.True);
        Assert.That(receipt.Admit(8, 0, out _), Is.EqualTo(TrainBaselineDisposition.Duplicate));
        Assert.That(receipt.TryComplete(8, 1, 3), Is.True);
    }

    [Test]
    public void HostManifestFailureRejectsBeforeAnySpawn()
    {
        TrainBaselineReceipt receipt = new();
        Assert.That(receipt.Begin(9, 4, 12, "trainset L-070 was invalid"), Is.False);
        Assert.That(receipt.Failure, Does.Contain("host could not construct"));
        Assert.That(receipt.Admit(9, 0, out _), Is.EqualTo(TrainBaselineDisposition.Reject));
    }

    [Test]
    public void EmptyWorldIsAValidCompletedBaseline()
    {
        TrainBaselineReceipt receipt = new();
        Assert.That(receipt.Begin(10, 0, 0, string.Empty), Is.True);
        Assert.That(receipt.TryComplete(10, 0, 0), Is.True);
        Assert.That(receipt.Complete, Is.True);
    }
}
