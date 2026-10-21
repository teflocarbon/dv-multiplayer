using Multiplayer.Core.Gadgets;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Gadgets;

[TestFixture]
public sealed class GadgetPredictionLogTests
{
    [Test]
    public void ProvisionalUids_AreNegativeAndNeverRepeat()
    {
        var log = new GadgetPredictionLog<string, string>();
        Assert.That(log.NewProvisionalUid(), Is.EqualTo(-1));
        Assert.That(log.NewProvisionalUid(), Is.EqualTo(-2));
        Assert.That(GadgetPredictionLog<string, string>.IsProvisional(-2), Is.True);
        Assert.That(GadgetPredictionLog<string, string>.IsProvisional(85), Is.False);
    }

    [Test]
    public void AConfirmation_KeepsThePredictionAndGivesItsProvisionalUid()
    {
        var log = new GadgetPredictionLog<string, string>();
        log.Predicted(1, "remove the plate", provisionalUid: -1);
        Assert.That(log.Confirm(1, out int provisional), Is.True);
        Assert.That(provisional, Is.EqualTo(-1));
        Assert.That(log.IsLive(1), Is.False);
        Assert.That(log.Reject(1), Is.Empty, "nothing left to undo");
    }

    [Test]
    public void ARejection_UndoesItAndEveryLaterPrediction_NewestFirst()
    {
        var log = new GadgetPredictionLog<string, string>();
        log.Predicted(1, "undo install");
        log.Predicted(2, "undo bolt");
        log.Predicted(3, "undo tape");
        Assert.That(log.Reject(1), Is.EqualTo(new[] { "undo tape", "undo bolt", "undo install" }));
        Assert.That(log.Count, Is.EqualTo(0));
    }

    [Test]
    public void AnEarlierPrediction_StaysWhenALaterOneIsRejected()
    {
        var log = new GadgetPredictionLog<string, string>();
        log.Predicted(1, "undo install");
        log.Predicted(2, "undo bolt");
        Assert.That(log.Reject(2), Is.EqualTo(new[] { "undo bolt" }));
        Assert.That(log.IsLive(1), Is.True);
    }

    [Test]
    public void APredictionUndoneByAnEarlierRejection_IsNotUndoneTwice()
    {
        var log = new GadgetPredictionLog<string, string>();
        log.Predicted(1, "undo install");
        log.Predicted(2, "undo bolt");
        log.Reject(1);
        Assert.That(log.WasUndone(2), Is.True);
        Assert.That(log.Reject(2), Is.Empty);
        Assert.That(log.WasUndone(2), Is.False);
    }

    [Test]
    public void APredictionUndoneAndThenConfirmed_IsNoLongerUndone()
    {
        var log = new GadgetPredictionLog<string, string>();
        log.Predicted(1, "undo install");
        log.Predicted(2, "undo wire");
        log.Reject(1);
        Assert.That(log.Confirm(2, out _), Is.False, "nothing live: the client applies the server's entry");
        Assert.That(log.WasUndone(2), Is.False);
    }

    [Test]
    public void ARequestNamingAProvisionalUid_IsHeldUntilItsInstallIsConfirmed()
    {
        var log = new GadgetPredictionLog<string, string>();
        int uid = log.NewProvisionalUid();
        log.Predicted(1, "undo install", uid);
        log.Hold(uid, "wire to it");
        log.Hold(uid, "mount on it");
        Assert.That(log.HasHeld(uid), Is.True);
        log.Confirm(1, out int provisional);
        Assert.That(log.Release(provisional), Is.EqualTo(new[] { "wire to it", "mount on it" }));
        Assert.That(log.HasHeld(uid), Is.False);
    }

    [Test]
    public void ARejectedInstall_DropsTheRequestsHeldForIt()
    {
        var log = new GadgetPredictionLog<string, string>();
        int uid = log.NewProvisionalUid();
        log.Predicted(1, "undo install", uid);
        log.Hold(uid, "wire to it");
        log.Reject(1);
        Assert.That(log.Release(uid), Is.Empty);
    }
}
