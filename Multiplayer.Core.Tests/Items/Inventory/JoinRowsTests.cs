using System;
using Multiplayer.Core.Items.Inventory;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Inventory;

[TestFixture]
public sealed class JoinRowsTests
{
    private static readonly Guid Owner = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Holder = new("22222222-2222-2222-2222-222222222222");

    [Test]
    public void AJoiningOwner_IsSentTheirEssentialAnotherPlayerHolds() =>
        Assert.That(JoinRows.SendsHeldEssential(Owner, Holder, 3, Owner), Is.True);

    [Test]
    public void NotSent_WithoutAClaim() =>
        Assert.That(JoinRows.SendsHeldEssential(Owner, Holder, -1, Owner), Is.False);

    [Test]
    public void NotSent_ToSomeoneWhoDoesntOwnIt() =>
        Assert.That(JoinRows.SendsHeldEssential(Owner, Holder, 3, Guid.NewGuid()), Is.False);

    [Test]
    public void NotSent_WhenTheJoinerHoldsIt()
    {
        // Their own inventory reaches them as rows.
        Assert.That(JoinRows.SendsHeldEssential(Owner, Owner, 3, Owner), Is.False);
    }

    [Test]
    public void NotSent_ForNobody() =>
        Assert.That(JoinRows.SendsHeldEssential(Guid.Empty, Holder, 3, Guid.Empty), Is.False);
}
