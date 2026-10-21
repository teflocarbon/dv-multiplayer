using DV.Customization.Gadgets.Implementations;
using DV.WeatherSystem;
using HarmonyLib;
using Multiplayer.Core.Gadgets;
using Multiplayer.Shared.Session;
using System;

namespace Multiplayer.Shared.Gadgets;

/// <summary>What a control panel's input does (ServerboundGadgetPanelPacket).</summary>
public enum GadgetPanelInput : byte
{
    /// <summary>A joystick moved a cab control one notch: the packet's control, the value's sign.</summary>
    Move = 1,
    Horn = 2,
    Starter = 3,
    PowerOff = 4
}

/// <summary>
/// The universal control stand (GadgetControlPanel) the same in every game (gadgets.md, "Shared motion
/// and randomness"). Vanilla rolls <c>Random.value</c> against the rain every 0.125 s on an exposed
/// panel, and while the roll faults the display blanks and the joysticks do nothing. In a session the
/// roll is the shared clock's 0.125 s step hashed with the panel's UID, against the rain rounded to
/// 0.05 (each game moves its weather on its own between the server's updates), so every game faults
/// at the same moments and the server can tell, with no LOD loaded, that a panel's input came while
/// it was faulted.
/// </summary>
[HarmonyPatch]
public static class GadgetControlPanels
{
    public const float FaultStepSeconds = 0.125f;

    private static bool InSession => NetworkLifecycle.Instance is { IsSessionRunning: true };

    /// <summary>The shared clock's fault step now.</summary>
    public static long Step() => (long)Math.Floor(GadgetSharedMotionPatches.SessionSeconds() / FaultStepSeconds);

    /// <summary>The rain an exposed panel faults against in this game, rounded as every game rounds it.</summary>
    public static float Rain(GadgetControlPanel panel) =>
        panel != null && panel.IsExposedToOutside && WeatherDriver.Instance != null
            ? GadgetSharedRandom.Coarse(WeatherDriver.Instance.RainValue.CurrentValue)
            : 0f;

    /// <summary>Whether the panel faults at <paramref name="step"/> with <paramref name="chance"/>.</summary>
    public static bool Faults(GadgetControlPanel panel, long step, float chance) =>
        chance > 0f && GadgetSharedRandom.Unit(step, panel.UID) < chance;

    /// <summary>Whether the panel is faulted now.</summary>
    public static bool Faulted(GadgetControlPanel panel) =>
        panel != null && InSession && Faults(panel, Step(), Rain(panel));

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetControlPanelLODObject), "SampleRandomFault")]
    private static bool SampleFault(GadgetControlPanelLODObject __instance, ref bool __result)
    {
        if (!InSession || __instance.Base == null)
            return true;
        __result = Faults(__instance.Base, Step(), GadgetSharedRandom.Coarse(__instance.faultChance));
        return false;
    }
}
