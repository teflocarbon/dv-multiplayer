using HarmonyLib;
using Multiplayer.Components.Networking.World;
using Multiplayer.Utils;
using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch(typeof(FlashlightItem))]
public static class FlashlightItemPatch
{
    [HarmonyPatch(nameof(FlashlightItem.Start))] 
    static void Postfix(FlashlightItem __instance)
    {
        var networkedItem = __instance.gameObject.GetOrAddComponent<NetworkedItem>();
        networkedItem.Initialize(__instance);

        // Register the values you want to track with both getters and setters
        networkedItem.RegisterTrackedValue(
            "originalLightIntensity",
            () => __instance.originalLightIntensity,
            value => __instance.originalLightIntensity = value,
            serverAuthoritative: true                           //This parameter is driven by the server: true
            );

        //probably not needed as flicker can be handled locally
        //networkedItem.RegisterTrackedValue(
        //    "intensity",
        //    () => __instance.spotlight.intensity,
        //    value =>  __instance.spotlight.intensity = value,
        //    serverAuthoritative: true                           //This parameter is driven by the server: true
        //    );

        networkedItem.RegisterTrackedValue(
            "originalBeamColour",
            () => __instance.originalBeamColor.ColorToUInt32(),
            value =>__instance.originalBeamColor = value.UInt32ToColor(),
            serverAuthoritative: true                           //This parameter is driven by the server: true
            );

        // The beam colour itself isn't synced: DV derives it each frame from originalBeamColor and the
        // battery (LowPowerFlicker), so every game works it out locally. Synced, a low battery on the
        // server's copy sent ~700 updates a minute, each a new item revision; the holder's own pickups
        // and stashes then arrived on a stale revision and were refused, and the corrections pulled
        // the flashlight off a VR belt (playtest 2026-10-19), and made a healthy light flicker.

        networkedItem.RegisterTrackedValue(
            "batteryPower",
            () => __instance.battery.currentPower,
            value =>
                {
                    __instance.battery.currentPower = value;     //set the value
                    __instance.battery.UpdatePower(0f);          //process a delta of 0 to force an update
                },
            (current, last) => (Math.Abs(current - last) >= 1.0f) || (current != last && (current == 0.0f || current == 1.0f)), //Don't communicate updates for changes less than 1.0f, unless hitting upper/lower limits
            // The holder's: it drains and charges (the solar panel) in their game. The server's copy
            // of a carried flashlight never sees the sun, and as the authority it ran the holder's
            // battery down on its own schedule (playtest 2026-10-19). Lying in the world, the
            // server's copy still reports it.
            serverAuthoritative: false
            );

        networkedItem.RegisterTrackedValue(
            "buttonState",
            () => (__instance.button.Value > 0f),
            value =>
                {
                    if (value)
                        __instance.button.SetValue(1f);
                    else
                        __instance.button.SetValue(0f);

                    __instance.ToggleFlashlight(value);
                }
            );

        networkedItem.FinaliseTrackedValues();
    }
}

/// <summary>
/// A flashlight that has never been active has not run Start, so it has no button yet, and its save
/// handler threw on it ("World item state save failed ... Object reference not set"). The server
/// keeps other players' inventory items inactive, so their flashlights never saved. Save the
/// battery and keep the switch as it was loaded.
/// </summary>
[HarmonyPatch(typeof(FlashlightItem), nameof(FlashlightItem.OnItemSaveDataRequested))]
public static class FlashlightItemSavePatch
{
    private static bool Prefix(FlashlightItem __instance, JObject data, ref JObject __result)
    {
        if (__instance.button != null)
            return true;
        if (__instance.battery != null)
            __instance.battery.SaveState(data);
        __result = data;
        return false;
    }
}

/// <summary>
/// Flashlights have unlimited battery: a flashlight's battery doesn't drain, and the first drain
/// tops it back up, so one loaded from a save flat or low comes back on at full power. DV's
/// BatteryConsumer drains every 0.1 s while the light is on; other items' batteries drain as usual.
/// </summary>
[HarmonyPatch(typeof(Battery), nameof(Battery.Drain))]
public static class FlashlightBatteryDrainPatch
{
    private static bool Prefix(Battery __instance)
    {
        if (__instance.GetComponent<FlashlightItem>() == null)
            return true;
        if (__instance.CurrentPower < Battery.MAX_POWER)
            __instance.Charge(Battery.MAX_POWER - __instance.CurrentPower);
        return false;
    }
}
