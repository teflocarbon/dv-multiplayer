using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class ItemStateObservationPolicyTests
{
    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.InInventory)]
    public void RemoteClientPlacement_IsPreservedOnHost(WireItemState state)
    {
        Assert.That(ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
            possessorPlayerId: 2, localPlayerId: 1, state), Is.True);
    }

    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.InInventory)]
    public void HostLocalPlacement_IsObservedFromUnity(WireItemState state)
    {
        Assert.That(ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
            possessorPlayerId: 1, localPlayerId: 1, state), Is.False);
    }

    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.InInventory)]
    public void HostPlacement_IsPreservedOnRemoteClient(WireItemState state)
    {
        Assert.That(ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
            possessorPlayerId: 1, localPlayerId: 2, state), Is.True);
    }

    [Test]
    public void WorldPlacement_IsNeverPreservedAsPlayerPlacement()
    {
        Assert.That(ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
            possessorPlayerId: 2, localPlayerId: 1, WireItemState.Dropped), Is.False);
    }

    [Test]
    public void UnknownLocalPlayer_DoesNotFreezePlacement()
    {
        Assert.That(ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
            possessorPlayerId: 1, localPlayerId: 0, WireItemState.InInventory), Is.False);
    }
}
