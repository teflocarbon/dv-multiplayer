using System.Collections;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// A boombox made from its row comes back as it was (D14, OI-COLD-13, OI-CON-16). A take-out (and
/// any copy made from a row: another game's, the server's) loads the row's state in the object's
/// first frame (<see cref="ItemStateText.LoadNowOrSoon"/>), before the boombox sets itself up at the
/// end of that frame. Vanilla's load waits a frame, so it never sees any of this:
/// <list type="bullet">
/// <item>its volume knob reports its own position to the listeners it just added, and the boombox's
/// volume became the knob's (0.3 came back 0.5). The loaded volume is set again a frame after the
/// boombox is set up, while its knob syncs with its listeners off, so the knob takes it;</item>
/// <item>its players never heard the power and mode it loaded: they subscribe as it sets up, after
/// the load told no one, so a boombox taken out on showed on and played nothing. They're told then;</item>
/// <item>vanilla starts a cassette as the boombox's container data loads, before a boombox made from
/// its row has its cassette (the cassette is made after it, <see cref="StandInAmmo"/>). Once it has
/// it, the cassette takes the row's track and position (<see cref="StashedCassette"/>), and plays
/// on if the row's did, as vanilla's load would.</item>
/// </list>
/// The server's copy of a take-out (<c>PlayerItems.Materialize</c>) loads a frame after its prefab,
/// as vanilla's own load does, so the boombox is set up by then and only its cassette, made after
/// the load there too, is left: a player host's own player heard nothing once the client took it out.
/// </summary>
[HarmonyPatch(typeof(BoomboxInteractionController))]
internal static class BoomboxStatePatch
{
    // How long a boombox made from its row waits for its cassette (another game's copy may get it later).
    private const float CassetteWaitSeconds = 10f;

    [HarmonyPatch(nameof(BoomboxInteractionController.OnItemSaveDataLoaded))]
    [HarmonyPostfix]
    private static void AfterLoad(BoomboxInteractionController __instance, JObject data)
    {
        if (!__instance.initialized)
        {
            float volume = __instance.volumeAndInterferenceController.GetVolume();
            Behaviours.BoomboxLiveState.HoldVolume(__instance, volume);
            CoroutineManager.Instance.StartCoroutine(AfterSetup(__instance, volume, data?.DeepClone() as JObject));
        }
        else if (!__instance.HasCassetteInserted && StashedCassette.TryRead(data, out _))
            CoroutineManager.Instance.StartCoroutine(Resume(__instance, data.DeepClone() as JObject));
    }

    private static IEnumerator AfterSetup(BoomboxInteractionController boombox, float volume, JObject data)
    {
        while (boombox != null && !boombox.initialized)
            yield return null;
        // The knob reports in the frame after the set-up; its listeners are off the frame after.
        yield return null;
        if (boombox == null)
            yield break;
        boombox.SetVolume(volume);
        TellPowerAndMode(boombox);
        yield return Resume(boombox, data);
    }

    // Once it has its cassette, the cassette takes the row's track and position, and plays on if the row's did.
    private static IEnumerator Resume(BoomboxInteractionController boombox, JObject data)
    {
        if (!StashedCassette.TryRead(data, out StashedCassette.Position position))
            yield break;
        float until = Time.realtimeSinceStartup + CassetteWaitSeconds;
        while (boombox != null && !boombox.HasCassetteInserted && Time.realtimeSinceStartup < until)
            yield return null;
        if (boombox == null || boombox.cassetteInteractionAreaScript.GetInsertedCassette() is not { } cassette)
            yield break;
        cassette.lastPlayedPlaylistEntry = position.Track;
        cassette.lastPlayedSeekPosition = position.Seek;
        // As vanilla's load: on in cassette mode it plays, else it waits for the power and the mode.
        if (position.Playing && !position.Ended && !boombox.cassetteController.IsPlaying)
            boombox.CassettePlay();
    }

    // What PowerAndModeController.PowerAndModeChanged tells each of them, told again.
    private static void TellPowerAndMode(BoomboxInteractionController boombox)
    {
        bool on = boombox.IsPoweredOn;
        bool radio = boombox.IsInRadioMode;
        boombox.volumeAndInterferenceController.OnPowerAndModeChanged(on, radio);
        boombox.radioController.OnPowerAndModeChanged(on, radio);
        // Only a change: a cassette already playing takes no second Turn_On.
        if (boombox.cassetteController.isOn != (on && !radio))
            boombox.cassetteController.OnPowerAndModeChanged(on, radio);
        if (boombox.GetComponent<BoomboxDisplayController>() is { } display)
            display.OnPowerAndModeChanged(on, radio);
    }
}
