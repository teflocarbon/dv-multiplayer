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
            isHost: true, possessorPlayerId: 2, hostPlayerId: 1, state), Is.True);
    }

    [TestCase(WireItemState.InHand)]
    [TestCase(WireItemState.InInventory)]
    public void HostLocalPlacement_IsObservedFromUnity(WireItemState state)
    {
        Assert.That(ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
            isHost: true, possessorPlayerId: 1, hostPlayerId: 1, state), Is.False);
    }

    [Test]
    public void WorldPlacement_IsNeverPreservedAsPlayerPlacement()
    {
        Assert.That(ItemStateObservationPolicy.PreserveRemotePlayerPlacement(
            isHost: true, possessorPlayerId: 2, hostPlayerId: 1, WireItemState.Dropped), Is.False);
    }
}
