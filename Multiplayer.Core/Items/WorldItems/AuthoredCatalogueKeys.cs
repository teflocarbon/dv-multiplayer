using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Multiplayer.Core.Items.WorldItems;

/// <summary>
/// The join check of scene-authored items (docs/design/items/world-items.md, "The authored
/// catalogue"). Each game names every key its scenes have, a collided one too (two objects that
/// make the same key), and the two must name the same keys. A collided key, on either side, is
/// refused alone: neither game binds it, and the server never sends that player an item with it.
/// Before R7 one collision on either side refused that client's whole catalogue, and it got no
/// scene item (shop and office props) all session (F46, OI-SYNC-08).
/// </summary>
public static class AuthoredCatalogueKeys
{
    /// <summary>The digest of a scene's keys (order doesn't matter).</summary>
    public static string Digest(IEnumerable<string> keys)
    {
        string material = string.Join("\n", keys.Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal));
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
        return BitConverter.ToString(hash, 0, 16).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>Whether two games have the same scene keys: the same count and digest.</summary>
    public static bool Match(int hostCount, string hostDigest, int clientCount, string clientDigest) =>
        hostCount == clientCount && string.Equals(hostDigest, clientDigest, StringComparison.Ordinal);

    /// <summary>The keys neither game binds for this player: what collides in either.</summary>
    public static HashSet<string> Refused(IEnumerable<string> hostCollisions, IEnumerable<string> clientCollisions)
    {
        var refused = new HashSet<string>(hostCollisions ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        refused.UnionWith(clientCollisions ?? Enumerable.Empty<string>());
        refused.Remove(null);
        return refused;
    }
}
