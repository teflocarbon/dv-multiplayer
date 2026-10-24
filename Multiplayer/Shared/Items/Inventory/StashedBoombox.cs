using System;
using System.Reflection;
using DV;
using DV.JObjectExtstensions;
using DV.Radio;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// A boombox's music while it's a row (docs/design/items/inventory.md, "Stashed items that act";
/// OI-COLD-12, OI-CON-16): vanilla's own players on a copy of the boombox's music source, from its
/// row's state. In radio mode, its radio (<c>RadioPlayerController</c>) on the row's station; in
/// cassette mode with its cassette playing, the cassette's playlist (<c>PlaylistPlayer</c>, as
/// <c>CassettePlayerController</c> plays it) from the row's track and position, to the end of the
/// tape. A stashed boombox's interference is paused in vanilla (its controller is disabled), so a
/// copy has none. Its holder's game plays it at their camera (<see cref="StashedItems"/>); every
/// other game near them, at their avatar, as a boombox in their hands would be
/// (<see cref="HeardStashedSounds"/>).
/// </summary>
internal sealed class StashedBoombox
{
    private const string PowerOnKey = "powerOn";
    private const string RadioModeKey = "radioMode";
    private const string VolumeKey = "vol";
    private const string StationKey = "rsIndex";

    private static readonly MethodInfo getPlaylist = AccessTools.Method(typeof(Cassette), "GetPlaylist");
    private static readonly MethodInfo setPlaylist = AccessTools.Method(typeof(PlaylistPlayer), "SetPlaylist");

    private GameObject host;
    private RadioPlayerController radio;
    private PlaylistPlayer cassette;
    private AudioSource source;
    private float lateSeconds;
    private bool resumeOnUnpause;

    /// <summary>What it plays: its station or its cassette, to tell a change from the same music.</summary>
    public string Plays { get; private set; } = string.Empty;

    /// <summary>The cassette moved to another track (its row keeps it).</summary>
    public event Action<int> TrackChanged;

    /// <summary>The cassette's tape ran out.</summary>
    public event Action RanOut;

    /// <summary>
    /// What a boombox's row plays, or empty for nothing: off, a cassette stopped, paused or run out,
    /// or none in it. <paramref name="cassettePrefab"/> is the cassette in its magazine, if any.
    /// </summary>
    public static string WhatPlays(JObject state, string cassettePrefab)
    {
        if (state == null || state.GetBool(PowerOnKey) != true)
            return string.Empty;
        if (state.GetBool(RadioModeKey) == true)
            return "radio:" + (state.GetInt(StationKey) ?? 0);
        return StashedCassette.TryRead(state, out StashedCassette.Position position) && position.Playing && !position.Ended &&
               StashedCassette.CassetteOfPrefab(cassettePrefab) != null
            ? "cassette:" + cassettePrefab
            : string.Empty;
    }

    /// <summary>
    /// Starts a boombox's music from its row under <paramref name="parent"/>, or null if it plays
    /// nothing. <paramref name="spatial"/> plays it in the world, as the boombox's own source is
    /// set; otherwise it's 2D, as a stashed item's sounds are. <paramref name="lateSeconds"/> is
    /// how long ago the row's position was the cassette's: it starts that much further on.
    /// </summary>
    public static StashedBoombox Start(string label, string boomboxPrefab, JObject state, string cassettePrefab,
        Transform parent, bool spatial, float lateSeconds = 0f)
    {
        string plays = WhatPlays(state, cassettePrefab);
        if (plays.Length == 0)
            return null;
        AudioSource music = InventoryStandIn.FindSpec(boomboxPrefab)?.GetComponent<BoomboxInteractionController>()?
            .volumeAndInterferenceController?.musicAudioSource;
        if (music == null)
            return null;
        GameObject host = new($"[MP] Stashed boombox {label}");
        host.transform.SetParent(parent, false);
        AudioSource source = StashedItems.CopySource(music, host, spatial);
        // As the boombox's own (BoomboxInteractionController.Awake).
        source.ignoreListenerPause = true;
        source.volume = Mathf.Clamp01(state.GetFloat(VolumeKey) ?? VolumeAndInterferenceController.DEFAULT_VOLUME);
        StashedBoombox boombox = new() { host = host, source = source, Plays = plays, lateSeconds = Mathf.Max(0f, lateSeconds) };
        if (state.GetBool(RadioModeKey) == true)
        {
            boombox.radio = new RadioPlayerController(host, source);
            boombox.radio.OverrideLastPlayedStationIndex(state.GetInt(StationKey) ?? 0);
            boombox.radio.OnPowerAndModeChanged(true, true);
        }
        else
        {
            StashedCassette.TryRead(state, out StashedCassette.Position position);
            boombox.StartCassette(StashedCassette.CassetteOfPrefab(cassettePrefab), position);
        }
        return boombox;
    }

    private void StartCassette(Cassette tape, StashedCassette.Position position)
    {
        cassette = host.AddComponent<PlaylistPlayer>();
        cassette.audioSource = source;
        // As CassettePlayerController sets it from the player's preference.
        cassette.StopOnFocusLost = !VRManager.IsVREnabled() && PausesInBackground();
        // The playlist type is PlaylistsNET's, which the mod doesn't reference.
        setPlaylist?.Invoke(cassette, new object[] { getPlaylist?.Invoke(tape, null), position.Track, position.Seek });
        cassette.TrackIndexChanged += track => TrackChanged?.Invoke(track);
        cassette.PlaylistEnded += () => RanOut?.Invoke();
        cassette.PlaybackStarted += CatchUp;
        cassette.Play();
    }

    // The player's "pause in background" preference (GamePreferences, in an assembly the mod doesn't reference).
    private static bool PausesInBackground()
    {
        try
        {
            Type preferences = AccessTools.TypeByName("GamePreferences");
            Type keys = AccessTools.TypeByName("Preferences");
            MethodInfo get = AccessTools.Method(preferences, "Get", new[] { keys }, new[] { typeof(bool) });
            return get?.Invoke(null, new[] { Enum.Parse(keys, "PauseInBackground") }) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A row's position from a while ago: once the track has loaded, it moves on by that much, short
    // of the track's last second (the next track starts from its own beginning).
    private void CatchUp()
    {
        cassette.PlaybackStarted -= CatchUp;
        AudioClip clip = source != null ? source.clip : null;
        if (lateSeconds <= 0f || clip == null)
            return;
        int target = source.timeSamples + Mathf.RoundToInt(lateSeconds * clip.frequency);
        if (target < clip.samples - clip.frequency)
            source.timeSamples = target;
    }

    /// <summary>Where its cassette has got to; false for a radio.</summary>
    public bool TryGetPosition(out int track, out long seek, out bool ranOut)
    {
        track = cassette != null ? cassette.GetTrackIndex() : 0;
        seek = cassette != null ? cassette.GetSeekPosition() : 0L;
        ranOut = StashedCassette.RanOut(cassette);
        return cassette != null;
    }

    public void SetVolume(float volume)
    {
        if (source != null)
            source.volume = Mathf.Clamp01(volume);
    }

    // As the boombox's own (BoomboxInteractionController.OnGamePaused, CassettePlayerController).
    public void OnGamePaused()
    {
        radio?.OnGamePaused();
        if (cassette != null && cassette.Pause())
            resumeOnUnpause = true;
    }

    public void OnGameUnpaused()
    {
        radio?.OnGameUnpaused();
        if (resumeOnUnpause && cassette != null)
            cassette.Play();
        resumeOnUnpause = false;
    }

    public void Stop()
    {
        // Gone with what it hung on (an avatar that left): its players went with it.
        if (host == null)
            return;
        radio?.TurnOff();
        if (cassette != null)
            cassette.Stop();
        if (host != null)
            Object.Destroy(host);
    }
}
