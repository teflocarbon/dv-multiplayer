using Multiplayer.Core.Gadgets;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Gadgets;

[TestFixture]
public sealed class GadgetChangeRecorderTests
{
    [Test]
    public void ATopLevelChange_IsRecordedWithTheCarsNextRevision()
    {
        var recorder = new GadgetChangeRecorder<string>();
        Assert.That(recorder.Enter(), Is.True);
        Assert.That(recorder.Record(5, GadgetChangeKind.Installed, "plate"), Is.EqualTo(1));
        recorder.Exit();

        var flushed = recorder.Flush();
        Assert.That(flushed, Has.Count.EqualTo(1));
        Assert.That(flushed[0].Anchor, Is.EqualTo(5));
        Assert.That(flushed[0].Revision, Is.EqualTo(1));
        Assert.That(flushed[0].Kind, Is.EqualTo(GadgetChangeKind.Installed));
    }

    [Test]
    public void APrimitiveInsideAnother_IsNotTopLevel()
    {
        var recorder = new GadgetChangeRecorder<string>();
        Assert.That(recorder.Enter(), Is.True);
        recorder.Record(5, GadgetChangeKind.Removed, "plate");
        Assert.That(recorder.Enter(), Is.False, "the light riding the plate comes off with it");
        Assert.That(recorder.Nested, Is.True);
        recorder.Exit();
        recorder.Exit();
        Assert.That(recorder.Nested, Is.False);
        Assert.That(recorder.Flush(), Has.Count.EqualTo(1));
    }

    [Test]
    public void EachCar_CountsItsOwnRevisions()
    {
        var recorder = new GadgetChangeRecorder<string>();
        recorder.Record(5, GadgetChangeKind.Installed, "a");
        recorder.Record(9, GadgetChangeKind.Installed, "b");
        recorder.Record(5, GadgetChangeKind.Removed, "a");
        Assert.That(recorder.Revision(5), Is.EqualTo(2));
        Assert.That(recorder.Revision(9), Is.EqualTo(1));
        Assert.That(recorder.Revision(3), Is.EqualTo(0));
    }

    [Test]
    public void ARevision_IsGivenWhenRecorded_SoASnapshotInTheSameTickCountsIt()
    {
        var recorder = new GadgetChangeRecorder<string>();
        recorder.Record(5, GadgetChangeKind.Installed, "a");
        Assert.That(recorder.Revision(5), Is.EqualTo(1), "before the flush");
        recorder.Flush();
        recorder.Record(5, GadgetChangeKind.Installed, "b");
        Assert.That(recorder.Flush()[0].Revision, Is.EqualTo(2), "revisions keep counting across flushes");
    }

    [Test]
    public void ChangesDuringARequest_CarryItsOperation()
    {
        var recorder = new GadgetChangeRecorder<string>();
        recorder.BeginOperation(4, 77);
        recorder.Record(5, GadgetChangeKind.Installed, "a");
        recorder.EndOperation();
        recorder.Record(5, GadgetChangeKind.Removed, "b");

        var flushed = recorder.Flush();
        Assert.That((flushed[0].PlayerId, flushed[0].OperationId), Is.EqualTo(((byte)4, 77u)));
        Assert.That((flushed[1].PlayerId, flushed[1].OperationId), Is.EqualTo(((byte)0, 0u)));
    }

    [Test]
    public void AForcedRemoval_SaysSo()
    {
        var recorder = new GadgetChangeRecorder<string>();
        recorder.Record(5, GadgetChangeKind.Removed, "a", forced: true);
        Assert.That(recorder.Flush()[0].Forced, Is.True);
    }

    [Test]
    public void Flush_EmptiesThePending()
    {
        var recorder = new GadgetChangeRecorder<string>();
        recorder.Record(5, GadgetChangeKind.Installed, "a");
        recorder.Flush();
        Assert.That(recorder.Flush(), Is.Empty);
    }

    [Test]
    public void AForgottenCar_StartsAgainAtOne()
    {
        var recorder = new GadgetChangeRecorder<string>();
        recorder.Record(5, GadgetChangeKind.Installed, "a");
        recorder.Forget(5);
        Assert.That(recorder.Record(5, GadgetChangeKind.Installed, "b"), Is.EqualTo(1));
    }

    [TestCase(3u, 2u, GadgetRevisionStep.Skip)]
    [TestCase(3u, 3u, GadgetRevisionStep.Skip)]
    [TestCase(3u, 4u, GadgetRevisionStep.Apply)]
    [TestCase(3u, 6u, GadgetRevisionStep.Gap)]
    [TestCase(0u, 1u, GadgetRevisionStep.Apply)]
    public void AClient_AppliesOnlyTheNextRevision(uint applied, uint incoming, GadgetRevisionStep expected) =>
        Assert.That(GadgetRevisionOrder.Decide(applied, incoming), Is.EqualTo(expected));
}
