namespace Multiplayer.Core.Gadgets;

/// <summary>
/// A roll every game makes the same way (gadgets.md, "Shared motion and randomness"): vanilla rolls
/// <c>Random.value</c> in each game, so two games disagree; this hashes a step of the session's shared
/// clock and the gadget's UID instead, so every game rolls the same number for the same step with no
/// traffic.
/// </summary>
public static class GadgetSharedRandom
{
    /// <summary>A number in [0, 1) for <paramref name="step"/> of the shared clock and gadget <paramref name="uid"/>.</summary>
    public static float Unit(long step, int uid)
    {
        unchecked
        {
            // SplitMix64 of the two together: neighbouring steps and UIDs land far apart.
            ulong z = (ulong)step * 0x9E3779B97F4A7C15UL + (ulong)(uint)uid * 0xBF58476D1CE4E5B9UL + 0x94D049BB133111EBUL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 40) / (float)(1UL << 24);
        }
    }

    /// <summary>
    /// <paramref name="chance"/> rounded to <paramref name="grain"/>: a value two games keep in step only
    /// roughly (the rain, which each game moves on its own between weather updates) gives the same
    /// answer in both unless it is right at a boundary.
    /// </summary>
    public static float Coarse(float chance, float grain = 0.05f) =>
        grain <= 0f ? chance : (float)System.Math.Round(chance / grain) * grain;
}
