using Multiplayer.Core.Gadgets;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Gadgets;

[TestFixture]
public sealed class GadgetSettingEchoTests
{
    private const byte Me = 4, Other = 3;

    [Test]
    public void NothingPending_ShowsTheServersValues()
    {
        GadgetSettingEchoDecision decision = GadgetSettingEcho.Decide(Me, 0, Other, 9, refused: false);

        Assert.That(decision.Action, Is.EqualTo(GadgetSettingEchoAction.Apply));
        Assert.That(decision.ClearsPending, Is.False);
    }

    [Test]
    public void AnswerToMyLatestChange_KeepsWhatIShowAndClearsIt()
    {
        GadgetSettingEchoDecision decision = GadgetSettingEcho.Decide(Me, 12, Me, 12, refused: false);

        Assert.That(decision.Action, Is.EqualTo(GadgetSettingEchoAction.KeepPrediction));
        Assert.That(decision.ClearsPending, Is.True);
    }

    [Test]
    public void RefusalOfMyLatestChange_PutsTheServersValuesBack()
    {
        GadgetSettingEchoDecision decision = GadgetSettingEcho.Decide(Me, 12, Me, 12, refused: true);

        Assert.That(decision.Action, Is.EqualTo(GadgetSettingEchoAction.Apply));
        Assert.That(decision.ClearsPending, Is.True);
    }

    [TestCase(Me, 11u, false)]   // my earlier change, answered while a newer one is on its way
    [TestCase(Me, 11u, true)]    // ... even refused: the newer one replaces it
    [TestCase(Other, 12u, false)] // someone else's, or the server's own (player 0)
    [TestCase((byte)0, 0u, false)]
    public void AnythingBeforeTheAnswer_KeepsMyPrediction(byte from, uint operation, bool refused)
    {
        GadgetSettingEchoDecision decision = GadgetSettingEcho.Decide(Me, 12, from, operation, refused);

        Assert.That(decision.Action, Is.EqualTo(GadgetSettingEchoAction.KeepPrediction));
        Assert.That(decision.ClearsPending, Is.False);
    }
}
