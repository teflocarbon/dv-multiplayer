using System;

namespace Multiplayer.Core.Gadgets;

/// <summary>
/// The range a gadget setting may take (gadgets.md, "Gadget state"): what vanilla's knob can reach,
/// from its first step to its last. The server refuses a request outside it rather than letting the
/// setter clamp it, so the requester's knob goes back to the server's value instead of to the clamp.
/// </summary>
public readonly struct GadgetSettingBounds
{
    public GadgetSettingBounds(float min, float max, bool whole)
    {
        Min = min;
        Max = max;
        Whole = whole;
    }

    public float Min { get; }
    public float Max { get; }
    /// <summary>The setting is a step (a mode, a channel, a toggle): whole numbers only.</summary>
    public bool Whole { get; }

    /// <summary>A toggle: 0 or 1.</summary>
    public static GadgetSettingBounds Toggle => new(0f, 1f, true);

    /// <summary>One of <paramref name="count"/> steps, 0 to count - 1.</summary>
    public static GadgetSettingBounds Steps(int count) => new(0f, Math.Max(0, count - 1), true);

    // A knob's position read back through float rounding.
    private const float Tolerance = 1e-3f;

    /// <summary>Why <paramref name="value"/> is out of these bounds, or null if it's within them.</summary>
    public string Refusal(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return "not a number";
        if (value < Min - Tolerance || value > Max + Tolerance)
            return $"{value:0.###} is outside {Min:0.###}..{Max:0.###}";
        if (Whole && Math.Abs(value - (float)Math.Round(value)) > Tolerance)
            return $"{value:0.###} is not a whole step";
        return null;
    }
}
