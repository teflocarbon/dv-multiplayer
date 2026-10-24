using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// A boombox made from its row keeps its volume (D14, OI-COLD-13). A take-out loads the row's
/// state in the object's first frame (<see cref="ItemStateText.LoadNowOrSoon"/>), before the
/// boombox sets itself up at the end of that frame; its volume knob then reports its own position
/// to the listeners it just added, and the boombox's volume became the knob's (0.3 came back 0.5).
/// Vanilla's load waits a frame, so it never sees this. The loaded volume is set again a frame
/// after the boombox is set up, while its knob syncs with its listeners off, so the knob takes it.
/// </summary>
[HarmonyPatch(typeof(BoomboxInteractionController))]
internal static class BoomboxStatePatch
{
    [HarmonyPatch(nameof(BoomboxInteractionController.OnItemSaveDataLoaded))]
    [HarmonyPostfix]
    private static void AfterLoad(BoomboxInteractionController __instance)
    {
        if (!__instance.initialized)
            CoroutineManager.Instance.StartCoroutine(VolumeAfterSetup(__instance, __instance.volumeAndInterferenceController.GetVolume()));
    }

    private static IEnumerator VolumeAfterSetup(BoomboxInteractionController boombox, float volume)
    {
        while (boombox != null && !boombox.initialized)
            yield return null;
        // The knob reports in the frame after the set-up; its listeners are off the frame after.
        yield return null;
        if (boombox != null)
            boombox.SetVolume(volume);
    }
}
