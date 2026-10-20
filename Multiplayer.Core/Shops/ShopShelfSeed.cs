namespace Multiplayer.Core.Shops;

/// <summary>
/// Which of a shop's verified shelf seeds every game uses this session. Vanilla's
/// <c>ShopShelves</c> picks one with <c>UnityEngine.Random</c>, so each game laid the shelves out
/// differently. The server picks a session seed and sends it at join; each game derives the same
/// choice from it and the shop's index, so the layout still changes from session to session.
/// </summary>
public static class ShopShelfSeed
{
    /// <summary>
    /// The index into the shop's verified seeds (<paramref name="verifiedSeedCount"/> of them) for
    /// the shop at <paramref name="shopIndex"/> in this session (<paramref name="sessionSeed"/>).
    /// </summary>
    public static int Pick(int sessionSeed, int shopIndex, int verifiedSeedCount)
    {
        if (verifiedSeedCount <= 0)
            return 0;

        // Mix the two so neighbouring shops don't step through the seed list together.
        unchecked
        {
            uint x = (uint)sessionSeed * 0x9E3779B1u ^ (uint)shopIndex * 0x85EBCA77u;
            x ^= x >> 15;
            x *= 0x2C1B3C6Du;
            x ^= x >> 12;
            return (int)(x % (uint)verifiedSeedCount);
        }
    }
}
