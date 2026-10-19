using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class ControlCommitEnvelopeTests
{
    private const byte A = 1, B = 2;
    private const ushort Car = 10, OtherCar = 11;

    private static ControlEnvelopeResult Change(ControlCommitEnvelope envelope, byte player, ulong operation,
        uint expected, uint current, ushort car = Car, bool inRange = true, bool mayWrite = true) =>
        envelope.Validate(player, car, inRange, mayWrite, expected, current, operation);

    [Test]
    public void AChangeAgainstTheCurrentRevision_IsAccepted()
    {
        Assert.That(Change(new ControlCommitEnvelope(), A, operation: 1, expected: 5, current: 5),
            Is.EqualTo(ControlEnvelopeResult.Accepted));
    }

    [Test]
    public void RangeIsCheckedFirst_ThenTheLease()
    {
        ControlCommitEnvelope envelope = new();
        Assert.That(Change(envelope, A, 1, 5, 5, inRange: false, mayWrite: false), Is.EqualTo(ControlEnvelopeResult.OutOfRange));
        Assert.That(Change(envelope, A, 1, 5, 5, mayWrite: false), Is.EqualTo(ControlEnvelopeResult.AuthorityDenied));
        // Neither refusal counted as the player's operation: the same one is still theirs to make.
        Assert.That(Change(envelope, A, 1, 4, 5), Is.EqualTo(ControlEnvelopeResult.Accepted));
    }

    [Test]
    public void ThePlayersNextOperations_AreAcceptedAgainstAnOlderRevision()
    {
        // A lever turned: several changes go before the first commit's revision comes back.
        ControlCommitEnvelope envelope = new();
        Assert.That(Change(envelope, A, 1, expected: 5, current: 5), Is.EqualTo(ControlEnvelopeResult.Accepted));
        Assert.That(Change(envelope, A, 2, expected: 5, current: 6), Is.EqualTo(ControlEnvelopeResult.Accepted));
        Assert.That(Change(envelope, A, 3, expected: 5, current: 7), Is.EqualTo(ControlEnvelopeResult.Accepted));
    }

    [Test]
    public void AnOutOfOrderOperation_AgainstAnOlderRevision_IsStale()
    {
        ControlCommitEnvelope envelope = new();
        Change(envelope, A, 5, 5, 5);
        Assert.That(Change(envelope, A, 4, expected: 5, current: 6), Is.EqualTo(ControlEnvelopeResult.StaleRevision));
        Assert.That(Change(envelope, A, 5, expected: 5, current: 6), Is.EqualTo(ControlEnvelopeResult.StaleRevision));
        // Operation 0 is no stream at all: only the current revision will do.
        Assert.That(Change(envelope, A, 0, expected: 5, current: 6), Is.EqualTo(ControlEnvelopeResult.StaleRevision));
        Assert.That(Change(envelope, A, 0, expected: 6, current: 6), Is.EqualTo(ControlEnvelopeResult.Accepted));
    }

    [Test]
    public void StreamsArePerPlayerAndPerCar()
    {
        ControlCommitEnvelope envelope = new();
        Change(envelope, A, 9, 5, 5);
        // Another player's low operation id and A's on another car start their own streams.
        Assert.That(Change(envelope, B, 1, expected: 4, current: 6), Is.EqualTo(ControlEnvelopeResult.Accepted));
        Assert.That(Change(envelope, A, 1, expected: 4, current: 6, car: OtherCar), Is.EqualTo(ControlEnvelopeResult.Accepted));
    }

    [Test]
    public void Forget_StartsAPlayersStreamsAgain()
    {
        ControlCommitEnvelope envelope = new();
        Change(envelope, A, 9, 5, 5);
        Change(envelope, B, 9, 5, 5);
        envelope.Forget(A);

        // A came back: its ids start again from 1.
        Assert.That(Change(envelope, A, 1, expected: 4, current: 6), Is.EqualTo(ControlEnvelopeResult.Accepted));
        Assert.That(Change(envelope, B, 1, expected: 4, current: 6), Is.EqualTo(ControlEnvelopeResult.StaleRevision));
    }
}
