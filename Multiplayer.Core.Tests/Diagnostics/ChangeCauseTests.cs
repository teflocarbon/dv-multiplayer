using Multiplayer.Core.Diagnostics;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Diagnostics;

[TestFixture]
public sealed class ChangeCauseTests
{
    private enum Reason : byte
    {
        Unknown = 0,
        Deleted = 1
    }

    [Test]
    public void ToString_NamesReasonPlayerAndDetail()
    {
        var cause = new ChangeCause<Reason>(Reason.Deleted, 3, "Bentgear29", 100, "mid-consist");

        Assert.That(cause.ToString(), Is.EqualTo("Deleted by Bentgear29 (mid-consist)"));
    }

    [Test]
    public void ToString_FallsBackToPlayerId_AndOmitsEmptyParts()
    {
        Assert.That(new ChangeCause<Reason>(Reason.Deleted, 3).ToString(), Is.EqualTo("Deleted by player 3"));
        Assert.That(new ChangeCause<Reason>(Reason.Unknown).ToString(), Is.EqualTo("Unknown"));
    }

    [Test]
    public void Default_HasNoPlayerAndEmptyText()
    {
        ChangeCause<Reason> cause = default;

        Assert.That(cause.HasPlayer, Is.False);
        Assert.That(cause.WithDetail("x").Detail, Is.EqualTo("x"));
    }
}
