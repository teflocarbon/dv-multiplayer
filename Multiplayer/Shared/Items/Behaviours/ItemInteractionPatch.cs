using DV.CabControls;
using DV.Hazmat;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Behaviours;

/// <summary>
/// Notices the local player working an item they don't hold, so its game may send that item's
/// object state (ItemObjectStateAuthority): grabbing or using one of its controls (a set-down
/// lantern's knob, a mounted gadget's switch), or lighting it with a flame they hold. A value
/// changing alone can't tell: every game's copy drifts (a lantern's knob is a physics body).
/// </summary>
internal static class ItemInteraction
{
    private static int localFlameDepth;

    /// <summary>The ignition running now comes from something the local player holds.</summary>
    internal static bool LocalFlame => localFlameDepth > 0;

    internal static bool BeginFlame(Component source)
    {
        NetworkedItem item = source != null ? source.GetComponentInParent<NetworkedItem>() : null;
        if (item == null || !item.IsLocallyPossessed())
            return false;
        localFlameDepth++;
        return true;
    }

    internal static void EndFlame(bool began)
    {
        if (began && localFlameDepth > 0)
            localFlameDepth--;
    }

    /// <summary>The item a control belongs to, if the control isn't the item's own grab.</summary>
    internal static NetworkedItem ItemOf(ControlImplBase control)
    {
        NetworkedItem item = control != null ? control.GetComponentInParent<NetworkedItem>() : null;
        return item != null && item.gameObject != control.gameObject ? item : null;
    }
}

[HarmonyPatch(typeof(ControlImplBase))]
internal static class ItemControlInteractionPatch
{
    [HarmonyPatch("FireGrabbed")]
    [HarmonyPostfix]
    private static void Grabbed(ControlImplBase __instance) =>
        ItemInteraction.ItemOf(__instance)?.BeginLocalInteraction();

    [HarmonyPatch("FireUngrabbed")]
    [HarmonyPostfix]
    private static void Ungrabbed(ControlImplBase __instance) =>
        ItemInteraction.ItemOf(__instance)?.EndLocalInteraction();

    [HarmonyPatch(nameof(ControlImplBase.Use))]
    [HarmonyPostfix]
    private static void Used(ControlImplBase __instance) =>
        ItemInteraction.ItemOf(__instance)?.TouchLocalInteraction();
}

// Clicking a held lighter (or lit lantern) on something to light it.
[HarmonyPatch(typeof(IgnitableUse), nameof(IgnitableUse.HandleUse))]
internal static class IgnitableUseInteractionPatch
{
    private static void Prefix(IgnitableUse __instance, out bool __state) =>
        __state = ItemInteraction.BeginFlame(__instance);

    private static void Finalizer(bool __state) => ItemInteraction.EndFlame(__state);
}

// A held flame lighting what it touches.
[HarmonyPatch(typeof(Igniter), nameof(Igniter.IgniteNow))]
internal static class IgniterInteractionPatch
{
    private static void Prefix(Igniter __instance, out bool __state) =>
        __state = ItemInteraction.BeginFlame(__instance);

    private static void Finalizer(bool __state) => ItemInteraction.EndFlame(__state);
}

[HarmonyPatch(typeof(Lantern), nameof(Lantern.Ignite))]
internal static class LanternIgnitedInteractionPatch
{
    private static void Postfix(Lantern __instance, bool __result)
    {
        if (__result && ItemInteraction.LocalFlame)
            __instance.GetComponent<NetworkedItem>()?.TouchLocalInteraction();
    }
}
