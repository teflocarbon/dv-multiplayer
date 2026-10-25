using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using DV.Radio;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Utils;
using UnityEngine;

namespace Multiplayer.Shared.Items.Behaviours;

/// <summary>
/// A boombox plays the same on every game's copy (docs/design/items/live-state.md, "Tracked
/// values"; everything heard in the world is shared). Its power, mode, volume, antenna and radio
/// station are tracked values, as a lantern's wick is, and so is its cassette: the track, whether
/// it plays, and where in the track it had got to. A copy made from a live Create gets them all
/// with it; before, it got none, and a boombox one player took out playing was silent on the
/// others' games.
/// <para>
/// The cassette's position runs on by itself on every game, and so does its track (each copy's
/// tape moves on to the next song). Neither is a change: the cassette's value changes only when
/// play, stop, pause, next or previous is pressed on this game (<see cref="Mark"/>), and carries
/// the position at that moment. A copy whose tape is within ~2 s of it plays on undisturbed.
/// </para>
/// <para>
/// A boombox sets itself up a frame after it wakes, and its knobs report their own positions the
/// frame after that (<see cref="Inventory.BoomboxStatePatch"/>). Values that arrive before then
/// wait, and the getters give them meanwhile, so the knobs' report isn't taken for a change.
/// </para>
/// </summary>
[HarmonyPatch(typeof(BoomboxInteractionController))]
internal static class BoomboxLiveState
{
    private const string PowerKey = "powerOn";
    private const string RadioKey = "radioMode";
    private const string VolumeKey = "vol";
    private const string AntennaKey = "antenna";
    private const string StationKey = "rsIndex";
    private const string CassetteKey = "mpCassette";

    // How far (in samples, ~2 s) a copy's tape may be from the sender's before it starts again there.
    private const long SeekTolerance = 96000;
    // How long a copy waits for its cassette (the cassette's Create may come after the boombox's).
    private const float CassetteWaitSeconds = 10f;

    private sealed class Sync
    {
        // Changed by a press of play, stop, pause, next or previous on this game; given by the sender otherwise.
        public int Mark;
        public bool Ready;
        public bool Applying;
        public bool WaitingForCassette;
        public bool? Power;
        public bool? Radio;
        public float? Volume;
        public float? Antenna;
        public int? Station;
        public string Cassette;
    }

    private static readonly ConditionalWeakTable<BoomboxInteractionController, Sync> syncs = new();
    private static readonly ConditionalWeakTable<CassettePlayerController, BoomboxInteractionController> boomboxOf = new();

    [HarmonyPatch(nameof(BoomboxInteractionController.Awake))]
    [HarmonyPostfix]
    private static void AfterAwake(BoomboxInteractionController __instance)
    {
        BoomboxInteractionController boombox = __instance;
        NetworkedItem item = boombox.GetOrAddComponent<NetworkedItem>();
        if (item == null)
        {
            Multiplayer.LogError("BoomboxLiveState: no NetworkedItem on " + boombox.name);
            return;
        }
        Sync sync = syncs.GetValue(boombox, _ => new Sync());
        boomboxOf.Remove(boombox.cassetteController);
        boomboxOf.Add(boombox.cassetteController, boombox);

        try
        {
            item.RegisterTrackedValue(PowerKey, () => sync.Power ?? boombox.IsPoweredOn,
                value => Set(boombox, sync, item, s => s.Power = value));
            item.RegisterTrackedValue(RadioKey, () => sync.Radio ?? boombox.IsInRadioMode,
                value => Set(boombox, sync, item, s => s.Radio = value));
            item.RegisterTrackedValue(VolumeKey, () => sync.Volume ?? boombox.volumeAndInterferenceController.GetVolume(),
                value => Set(boombox, sync, item, s => s.Volume = value), (current, last) => Mathf.Abs(current - last) >= 0.02f);
            item.RegisterTrackedValue(AntennaKey, () => sync.Antenna ?? boombox.volumeAndInterferenceController.GetAntennaControlValue(),
                value => Set(boombox, sync, item, s => s.Antenna = value), (current, last) => Mathf.Abs(current - last) >= 0.02f);
            item.RegisterTrackedValue(StationKey, () => sync.Station ?? boombox.radioController.CurrentStationIndex,
                value => Set(boombox, sync, item, s => s.Station = value));
            item.RegisterTrackedValue(CassetteKey, () => sync.Cassette ?? CassetteValue(boombox, sync),
                value => Set(boombox, sync, item, s => s.Cassette = value), (current, last) => MarkOf(current) != MarkOf(last));
            item.FinaliseTrackedValues();
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"BoomboxLiveState: {ex.Message}\r\n{ex.StackTrace}");
        }

        CoroutineManager.Instance.StartCoroutine(WhenSetUp(boombox, sync, item));
    }

    /// <summary>
    /// A boombox made from its row: the volume it loaded, until its set-up is over. Its knob reports
    /// its own position meanwhile, and that isn't a change of volume.
    /// </summary>
    internal static void HoldVolume(BoomboxInteractionController boombox, float volume)
    {
        if (syncs.TryGetValue(boombox, out Sync sync) && !sync.Ready)
            sync.Volume = volume;
    }

    private static IEnumerator WhenSetUp(BoomboxInteractionController boombox, Sync sync, NetworkedItem item)
    {
        while (boombox != null && !boombox.initialized)
            yield return null;
        // The knobs report in the frame after the set-up.
        yield return null;
        if (boombox == null)
            yield break;
        sync.Ready = true;
        Apply(boombox, sync, item);
    }

    private static void Set(BoomboxInteractionController boombox, Sync sync, NetworkedItem item, Action<Sync> value)
    {
        if (boombox == null)
            return;
        value(sync);
        if (sync.Ready)
            Apply(boombox, sync, item);
    }

    // What has arrived, applied as vanilla's own controls would set it, and the knobs moved to match.
    private static void Apply(BoomboxInteractionController boombox, Sync sync, NetworkedItem item)
    {
        bool controls = false;
        sync.Applying = true;
        try
        {
            if (sync.Power is bool on)
            {
                sync.Power = null;
                if (boombox.IsPoweredOn != on)
                    boombox.SetPower(on);
                controls = true;
            }
            if (sync.Radio is bool radio)
            {
                sync.Radio = null;
                if (boombox.IsInRadioMode != radio)
                    boombox.SetMode(radio);
                controls = true;
            }
            if (sync.Volume is float volume)
            {
                sync.Volume = null;
                boombox.SetVolume(volume);
                controls = true;
            }
            if (sync.Antenna is float antenna)
            {
                sync.Antenna = null;
                boombox.SetAntenna(antenna);
                controls = true;
            }
            if (sync.Station is int station)
            {
                sync.Station = null;
                Tune(boombox.radioController, station);
            }
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"BoomboxLiveState.Apply({boombox.name}): {ex.Message}\r\n{ex.StackTrace}");
        }
        finally
        {
            sync.Applying = false;
        }
        if (controls)
            MoveControls(boombox, item);
        if (sync.Cassette != null && !TryApplyCassette(boombox, sync) && !sync.WaitingForCassette)
            CoroutineManager.Instance.StartCoroutine(WaitForCassette(boombox, sync));
    }

    // Vanilla's own sync of its knobs to its state (as after a load), unless our player is working them.
    private static void MoveControls(BoomboxInteractionController boombox, NetworkedItem item)
    {
        if (item != null && item.IsLocalInteractionOpen)
            return;
        if (!boombox.gameObject.activeInHierarchy)
            boombox.syncInteractablesFlag = true;
        else if (boombox.syncInteractablesCoro == null)
            boombox.SyncInteractables();
    }

    private static void Tune(RadioPlayerController radio, int station)
    {
        if (radio.CurrentStationIndex == station)
            return;
        if (radio.player == null)
        {
            radio.OverrideLastPlayedStationIndex(station);
            return;
        }
        // Its station is chosen as it turns on.
        radio.TurnOff();
        radio.OverrideLastPlayedStationIndex(station);
        radio.TurnOn();
    }

    private static IEnumerator WaitForCassette(BoomboxInteractionController boombox, Sync sync)
    {
        sync.WaitingForCassette = true;
        float until = Time.realtimeSinceStartup + CassetteWaitSeconds;
        while (boombox != null && sync.Cassette != null && !boombox.HasCassetteInserted && Time.realtimeSinceStartup < until)
            yield return null;
        sync.WaitingForCassette = false;
        if (boombox == null || sync.Cassette == null)
            yield break;
        if (!TryApplyCassette(boombox, sync))
            sync.Cassette = null;   // no cassette came: the value goes, and the getter reads the boombox again
    }

    // False while the boombox has no cassette to apply it to.
    private static bool TryApplyCassette(BoomboxInteractionController boombox, Sync sync)
    {
        if (!TryParse(sync.Cassette, out int mark, out int track, out bool playing, out long seek))
        {
            sync.Cassette = null;
            return true;
        }
        Cassette cassette = boombox.cassetteInteractionAreaScript.GetInsertedCassette();
        if (cassette == null)
            return false;
        sync.Cassette = null;
        sync.Mark = mark;
        CassettePlayerController player = boombox.cassetteController;
        if (playing && player.IsPlaying && player.CurrentPlaylistIndex == track && Math.Abs(SeekOf(player, cassette) - seek) <= SeekTolerance)
            return true;
        sync.Applying = true;
        try
        {
            // Stop, not pause: from stopped (door closed) play starts it again, and a second stop ejects.
            if (player.IsPlaying)
                player.StopOrEject();
            cassette.lastPlayedPlaylistEntry = track;
            cassette.lastPlayedSeekPosition = seek;
            if (playing)
                player.Play();
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"BoomboxLiveState.TryApplyCassette({boombox.name}): {ex.Message}\r\n{ex.StackTrace}");
        }
        finally
        {
            sync.Applying = false;
        }
        return true;
    }

    // "mark|track|playing|seek": the mark of the last press, and the tape as it is now.
    private static string CassetteValue(BoomboxInteractionController boombox, Sync sync)
    {
        CassettePlayerController player = boombox.cassetteController;
        Cassette cassette = boombox.cassetteInteractionAreaScript != null ? boombox.cassetteInteractionAreaScript.GetInsertedCassette() : null;
        return string.Join("|", sync.Mark.ToString(CultureInfo.InvariantCulture),
            player.CurrentPlaylistIndex.ToString(CultureInfo.InvariantCulture), player.IsPlaying ? "1" : "0",
            SeekOf(player, cassette).ToString(CultureInfo.InvariantCulture));
    }

    private static long SeekOf(CassettePlayerController player, Cassette cassette) =>
        player.player != null ? player.player.GetSeekPosition() : cassette != null ? cassette.lastPlayedSeekPosition : 0L;

    private static int MarkOf(string value) =>
        TryParse(value, out int mark, out _, out _, out _) ? mark : 0;

    private static bool TryParse(string value, out int mark, out int track, out bool playing, out long seek)
    {
        mark = track = 0;
        playing = false;
        seek = 0L;
        string[] parts = value?.Split('|');
        if (parts is not { Length: 4 } ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out mark) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out track) ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out seek))
            return false;
        playing = parts[2] == "1";
        return true;
    }

    /// <summary>A press on this game's cassette player: a new mark, so the cassette's value has changed.</summary>
    internal static void Pressed(CassettePlayerController player)
    {
        if (!boomboxOf.TryGetValue(player, out BoomboxInteractionController boombox) || boombox == null ||
            !syncs.TryGetValue(boombox, out Sync sync) || sync.Applying)
            return;
        int mark;
        do
            mark = UnityEngine.Random.Range(1, int.MaxValue);
        while (mark == sync.Mark);
        sync.Mark = mark;
    }
}

/// <summary>A press on a boombox's cassette player (its buttons, a click on the boombox, a load that plays it).</summary>
[HarmonyPatch]
internal static class BoomboxCassettePressPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CassettePlayerController), nameof(CassettePlayerController.Play));
        yield return AccessTools.Method(typeof(CassettePlayerController), nameof(CassettePlayerController.StopOrEject));
        yield return AccessTools.Method(typeof(CassettePlayerController), nameof(CassettePlayerController.Pause));
        yield return AccessTools.Method(typeof(CassettePlayerController), nameof(CassettePlayerController.Next));
        yield return AccessTools.Method(typeof(CassettePlayerController), nameof(CassettePlayerController.Previous));
    }

    private static void Prefix(CassettePlayerController __instance) => BoomboxLiveState.Pressed(__instance);
}
