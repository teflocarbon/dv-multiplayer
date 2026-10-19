using HarmonyLib;
using Multiplayer.Shared.Utils;
using System;

namespace Multiplayer.Shared.Items;

[HarmonyPatch(typeof(Lantern))]
public static class LanternPatch
{
    [HarmonyPatch(nameof(Lantern.Awake))]
    [HarmonyPostfix]
    static void Awake(Lantern __instance)
    {
        var networkedItem = __instance?.gameObject?.GetOrAddComponent<NetworkedItem>();
        if (networkedItem == null)
        {
            Multiplayer.LogError($"LanternAwakePatch.Awake() networkedItem returned null!");
            return;
        }

        networkedItem.Initialize(__instance);
    }

    [HarmonyPatch(nameof(Lantern.Initialize))]
    [HarmonyPostfix]
    static void Initialize(Lantern __instance)
    {

        var networkedItem = __instance?.gameObject?.GetOrAddComponent<NetworkedItem>();

        if (networkedItem == null)
        {
            Multiplayer.LogError($"Lantern.Initialize() networkedItem Not Found!");
            return;
        }

        try
        {
            // Register the values you want to track with both getters and setters
            networkedItem.RegisterTrackedValue
            (
                "wickSize",
                () => __instance.wickSize,
                value =>
                {
                    __instance.UpdateWickRelatedLogic(value);
                    // The wick follows the knob, a physics body: left where it was, the knob's next
                    // movement set the wick back. Not while our player is turning it.
                    if (__instance.knob != null && !networkedItem.IsLocalInteractionOpen)
                        __instance.knob.SetValue(value);
                }
            );

            networkedItem.RegisterTrackedValue
            (
                "Ignited",
                () => __instance.igniter.enabled,
                value =>
                        {
                            if (value)
                                __instance.Ignite(1);
                            else
                                __instance.OnFlameExtinguished();
                        }
            );

            networkedItem.FinaliseTrackedValues();

        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"Lantern.Initialize() {ex.Message}\r\n{ex.StackTrace}");
        }
    }
}

