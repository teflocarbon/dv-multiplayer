using System;

namespace Multiplayer.Core.Gadgets;

/// <summary>
/// How far one notch of a gadget's <c>Controls.Move</c> moves a cab control that isn't notched, as a
/// fraction of its range (gadgets.md, "Logic that writes the train"). With the cab loaded, vanilla
/// scrolls the cab's lever one mouse-wheel step per notch; with none (a dedicated host always, a
/// player host for cabs its player isn't in) it moves the control by a whole range, 0 to 1 at once.
/// The server steps instead by the lever's own scroll step, read from the livery's cab prefab.
/// </summary>
public static class GadgetControlStep
{
    /// <summary>When the cab has no spec this understands: a tenth of the range.</summary>
    public const float Fallback = 0.1f;

    /// <summary>A lever (LeverBase.ScrollWheelRotate): <paramref name="hoverScroll"/> notches of a
    /// stepped joint, or degrees of its hinge's range.</summary>
    public static float Lever(float hoverScroll, bool stepped, int notches, float limitMin, float limitMax)
    {
        float scroll = Math.Abs(hoverScroll);
        if (stepped && notches > 1)
            return Clamp(scroll / (notches - 1));
        float range = Math.Abs(limitMax - limitMin);
        return range > 0f ? Clamp(scroll / range) : Fallback;
    }

    /// <summary>A puller (PullerBase.Scroll): its scroll is already a fraction of its travel.</summary>
    public static float Puller(float hoverScroll) => Clamp(Math.Abs(hoverScroll));

    private static float Clamp(float step) =>
        float.IsNaN(step) || step <= 0f ? Fallback : Math.Min(step, 1f);
}
