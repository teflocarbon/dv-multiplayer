using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class ItemWireStateProjectionTests
{
    private static readonly WireItemState[] States =
    {
        WireItemState.Dropped,
        WireItemState.Thrown,
        WireItemState.InInventory,
        WireItemState.InHand,
        WireItemState.Attached,
        WireItemState.Removed
    };

    [TestCaseSource(nameof(States))]
    public void MissingBaseline_AlwaysRequiresSend(WireItemState state)
    {
        Assert.That(ItemWireStateComparer.RequiresSend(false, default,
            Projection(state)), Is.True);
    }

    [Test]
    public void EveryDifferentStatePair_RequiresSend()
    {
        foreach (WireItemState previous in States)
        foreach (WireItemState current in States)
        {
            if (previous == current) continue;
            Assert.That(ItemWireStateComparer.RequiresSend(true,
                Projection(previous), Projection(current)), Is.True,
                $"{previous} -> {current}");
        }
    }

    [TestCase(WireItemState.Dropped)]
    [TestCase(WireItemState.Thrown)]
    [TestCase(WireItemState.Removed)]
    public void WorldLikeStates_IgnoreUnusedHolderAndAttachmentFields(WireItemState state)
    {
        ItemWireStateProjection previous = new(state, placementPlayerId: 1,
            attachedCarNetId: 10, attachedFront: false);
        ItemWireStateProjection current = new(state, placementPlayerId: 2,
            attachedCarNetId: 99, attachedFront: true);

        Assert.That(ItemWireStateComparer.RequiresSend(true, previous, current), Is.False);
    }

    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.InInventory)]
    public void PlayerPlacement_HolderChangeRequiresSend(WireItemState state)
    {
        Assert.That(ItemWireStateComparer.RequiresSend(true,
            new ItemWireStateProjection(state, 1),
            new ItemWireStateProjection(state, 2)), Is.True);
    }

    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.InInventory)]
    public void PlayerPlacement_IgnoresUnusedAttachmentFields(WireItemState state)
    {
        Assert.That(ItemWireStateComparer.RequiresSend(true,
            new ItemWireStateProjection(state, 2, 10, false),
            new ItemWireStateProjection(state, 2, 99, true)), Is.False);
    }

    [Test]
    public void Attachment_CarChangeRequiresSend()
    {
        Assert.That(ItemWireStateComparer.RequiresSend(true,
            new ItemWireStateProjection(WireItemState.Attached, attachedCarNetId: 10),
            new ItemWireStateProjection(WireItemState.Attached, attachedCarNetId: 11)), Is.True);
    }

    [Test]
    public void Attachment_EndChangeRequiresSend()
    {
        Assert.That(ItemWireStateComparer.RequiresSend(true,
            new ItemWireStateProjection(WireItemState.Attached, attachedCarNetId: 10, attachedFront: false),
            new ItemWireStateProjection(WireItemState.Attached, attachedCarNetId: 10, attachedFront: true)), Is.True);
    }

    [Test]
    public void Attachment_IgnoresUnusedPlayerId()
    {
        Assert.That(ItemWireStateComparer.RequiresSend(true,
            new ItemWireStateProjection(WireItemState.Attached, 1, 10, true),
            new ItemWireStateProjection(WireItemState.Attached, 2, 10, true)), Is.False);
    }

    [TestCaseSource(nameof(States))]
    public void IdenticalProjection_DoesNotRequireSend(WireItemState state)
    {
        ItemWireStateProjection value = Projection(state);
        Assert.That(ItemWireStateComparer.RequiresSend(true, value, value), Is.False);
    }

    private static ItemWireStateProjection Projection(WireItemState state) => state switch
    {
        WireItemState.InHand or WireItemState.InInventory => new(state, 2),
        WireItemState.Attached => new(state, attachedCarNetId: 44, attachedFront: true),
        _ => new ItemWireStateProjection(state)
    };
}
