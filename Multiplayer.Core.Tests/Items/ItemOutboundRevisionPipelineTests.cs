using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class ItemOutboundRevisionPipelineTests
{
    [Test]
    public void RapidReliableOrderedTransitions_ReserveConsecutiveExpectedRevisions()
    {
        ItemOutboundRevisionPipeline pipeline = new();

        Assert.That(pipeline.Reserve(3), Is.EqualTo(3));
        Assert.That(pipeline.Reserve(3), Is.EqualTo(4));
        Assert.That(pipeline.Reserve(3), Is.EqualTo(5));
    }

    [Test]
    public void OwnAcknowledgement_DoesNotRewindOutstandingReservations()
    {
        ItemOutboundRevisionPipeline pipeline = new();
        Assert.That(pipeline.Reserve(3), Is.EqualTo(3));
        Assert.That(pipeline.Reserve(3), Is.EqualTo(4));

        pipeline.ObserveCanonical(4, preserveReservations: true);

        Assert.That(pipeline.Reserve(4), Is.EqualTo(5));
    }

    [Test]
    public void HostCorrection_ResetsSpeculativeRevisionChain()
    {
        ItemOutboundRevisionPipeline pipeline = new();
        Assert.That(pipeline.Reserve(7), Is.EqualTo(7));
        Assert.That(pipeline.Reserve(7), Is.EqualTo(8));

        pipeline.ObserveCanonical(7, preserveReservations: false);

        Assert.That(pipeline.Reserve(7), Is.EqualTo(7));
    }

    [Test]
    public void RevisionNeverWraps()
    {
        ItemOutboundRevisionPipeline pipeline = new();

        Assert.That(pipeline.Reserve(uint.MaxValue), Is.EqualTo(uint.MaxValue));
        Assert.That(pipeline.Reserve(uint.MaxValue), Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void Reset_StartsFreshNetworkLifetime()
    {
        ItemOutboundRevisionPipeline pipeline = new();
        Assert.That(pipeline.Reserve(12), Is.EqualTo(12));
        Assert.That(pipeline.Reserve(12), Is.EqualTo(13));

        pipeline.Reset();

        Assert.That(pipeline.Reserve(1), Is.EqualTo(1));
        Assert.That(pipeline.Reserve(1), Is.EqualTo(2));
    }
}
