using Multiplayer.Core.Items.WorldItems;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.WorldItems;

[TestFixture]
public sealed class AuthoredCatalogueKeysTests
{
    [Test]
    public void TheDigestIsOfTheKeysInAnyOrder() =>
        Assert.That(AuthoredCatalogueKeys.Digest(new[] { "b", "a", "c" }),
            Is.EqualTo(AuthoredCatalogueKeys.Digest(new[] { "c", "b", "a", "a" })));

    [Test]
    public void OtherKeysAreAnotherDigest() =>
        Assert.That(AuthoredCatalogueKeys.Digest(new[] { "a", "b" }),
            Is.Not.EqualTo(AuthoredCatalogueKeys.Digest(new[] { "a", "c" })));

    [Test]
    public void TheSameKeysMatch()
    {
        string digest = AuthoredCatalogueKeys.Digest(new[] { "a", "b" });
        Assert.That(AuthoredCatalogueKeys.Match(2, digest, 2, digest), Is.True);
        Assert.That(AuthoredCatalogueKeys.Match(2, digest, 3, digest), Is.False);
        Assert.That(AuthoredCatalogueKeys.Match(2, digest, 2, AuthoredCatalogueKeys.Digest(new[] { "a" })), Is.False);
    }

    [Test]
    public void ACollisionInEitherGameIsRefusedAlone()
    {
        // F46: the client's game makes "b" twice; the host's makes "c" twice. Only those two go.
        var refused = AuthoredCatalogueKeys.Refused(new[] { "c" }, new[] { "b", "c" });
        Assert.That(refused, Is.EquivalentTo(new[] { "b", "c" }));
        Assert.That(refused.Contains("a"), Is.False);
    }

    [Test]
    public void NoCollisionsRefuseNothing() =>
        Assert.That(AuthoredCatalogueKeys.Refused(null, new string[0]), Is.Empty);
}
