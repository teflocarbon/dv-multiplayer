using System.Collections.Generic;
using System.Linq;
using DV;
using DV.TimeKeeping;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// What a stashed item still does while it's a row (docs/design/items/inventory.md, "Stashed items
/// that act"; D14, OI-COLD-11 and OI-COLD-12). Vanilla keeps a stashed object alive, and three
/// things of it carry on in a pocket; with no object, the local player's game does them from the
/// stand-ins' rows, at the player's camera as vanilla's stashed sounds are
/// (<c>ItemPositionHandler</c>):
/// <list type="bullet">
/// <item>an alarm clock the player owns shows the HUD's time in a cab (<c>PlayerHasClock</c>);</item>
/// <item>a set alarm clock counts down and rings (<see cref="AlarmRows"/>);</item>
/// <item>a boombox left on keeps playing at its volume: its radio on its station, or its cassette
/// from where it had got to, to the end of the tape (<see cref="StashedBoombox"/>). Its row keeps
/// the cassette's track as it moves on, and the server is told (for the players near its holder,
/// who hear it too, <see cref="HeardStashedSounds"/>, and for its object made again); a take-out
/// resumes where it had got to.</item>
/// </list>
/// A silhouette's item is elsewhere, so it does none of these: its object does.
/// </summary>
[HarmonyPatch]
internal static class StashedItems
{
    private static readonly AlarmRows.Ticker<ushort> alarms = new();
    private static readonly Dictionary<ushort, StashedBoombox> boomboxes = new();
    private static readonly Dictionary<string, bool> boomboxPrefabs = new();
    // A boombox follows its row a few times a second: a take-out stops it within this.
    private const float RadioCheckSeconds = 0.25f;
    private static float nextRadioCheck;
    private static Transform soundRoot;
    private static AppUtil pauseSource;

    /// <summary>The local player's stand-ins that hold their items: not silhouettes.</summary>
    private static IEnumerable<InventoryStandIn> Stashed =>
        InventoryStandIn.All.Where(standIn => standIn.Known && !InventorySilhouettes.IsSilhouette(standIn));

    [HarmonyPatch(typeof(WorldClockController), nameof(WorldClockController.PlayerHasClock), MethodType.Getter)]
    [HarmonyPostfix]
    private static void PlayerHasClock(ref bool __result)
    {
        // A clock registers itself as the player's when its object starts; a stashed one has none.
        if (!__result)
            __result = Stashed.Any(standIn => standIn.Relation == ItemRelation.Mine && AlarmRows.IsAlarmClock(standIn.PrefabName));
    }

    /// <summary>
    /// Every frame on a game with a local player: alarms tick on the world's minute, boomboxes
    /// follow their rows, and other players' stashed boomboxes follow them.
    /// </summary>
    public static void Update()
    {
        HeardStashedSounds.Update();
        WatchPause();
        if (InventoryRowHolder.Current == null)
        {
            StopAll();
            return;
        }
        if (alarms.NextMinute(out System.DateTime now))
            TickAlarms(now);
        if (Time.unscaledTime >= nextRadioCheck)
        {
            nextRadioCheck = Time.unscaledTime + RadioCheckSeconds;
            UpdateBoomboxes();
        }
    }

    private static void TickAlarms(System.DateTime now)
    {
        HashSet<ushort> live = new();
        foreach (InventoryStandIn standIn in Stashed)
        {
            if (!AlarmRows.IsAlarmClock(standIn.PrefabName) || string.IsNullOrEmpty(standIn.State))
                continue;
            live.Add(standIn.NetId);
            if (Parse(standIn) is not { } state)
                continue;
            AlarmRows.Tick tick = alarms.Advance(standIn.NetId, state, now);
            if (tick == AlarmRows.Tick.None)
                continue;
            standIn.State = state.ToString(Newtonsoft.Json.Formatting.None);
            AnalogAlarmClockAudioController sounds = standIn.Prefab.GetComponentInChildren<AnalogAlarmClockAudioController>(true);
            if (sounds != null)
                PlayOnce(tick == AlarmRows.Tick.Rang ? sounds.alarmAudioSource : sounds.incrementAudioSource);
            Trace(tick == AlarmRows.Tick.Rang ? "item.stashed-alarm-rang" : "item.stashed-alarm-turned", standIn.NetId,
                new() { ["prefab"] = standIn.PrefabName });
        }
        alarms.KeepOnly(live);
    }

    private static JObject Parse(InventoryStandIn standIn)
    {
        try
        {
            return JObject.Parse(standIn.State);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    internal static void Trace(string eventName, ushort netId, Dictionary<string, object> data)
    {
        if (DebugRuntime.EnabledFor("item"))
            DebugRuntime.Publish("item", eventName, NetworkRole.DebugSide, entityType: "Item", entityId: netId.ToString(), data: data);
    }

    internal static bool IsBoombox(string prefabName)
    {
        if (string.IsNullOrEmpty(prefabName))
            return false;
        if (!boomboxPrefabs.TryGetValue(prefabName, out bool boombox))
        {
            InventoryItemSpec spec = InventoryStandIn.FindSpec(prefabName);
            boombox = spec != null && spec.GetComponent<BoomboxInteractionController>() != null;
            boomboxPrefabs[prefabName] = boombox;
        }
        return boombox;
    }

    private static void UpdateBoomboxes()
    {
        HashSet<ushort> playing = null;
        foreach (InventoryStandIn standIn in Stashed)
        {
            if (!IsBoombox(standIn.PrefabName) || string.IsNullOrEmpty(standIn.State) || Parse(standIn) is not { } state)
                continue;
            string cassette = CassettePrefabOf(standIn.State);
            string plays = StashedBoombox.WhatPlays(state, cassette);
            if (plays.Length == 0)
                continue;
            (playing ??= new HashSet<ushort>()).Add(standIn.NetId);
            if (boomboxes.TryGetValue(standIn.NetId, out StashedBoombox running))
            {
                if (running.Plays == plays)
                    continue;
                running.Stop();
                boomboxes.Remove(standIn.NetId);
            }
            StashedBoombox boombox = StashedBoombox.Start(standIn.NetId.ToString(), standIn.PrefabName, state, cassette, SoundRoot, false);
            if (boombox == null)
                continue;
            ushort netId = standIn.NetId;
            boombox.TrackChanged += _ => KeepPosition(netId, "track");
            boombox.RanOut += () => KeepPosition(netId, "ran-out");
            boomboxes.Add(netId, boombox);
            Trace("item.stashed-boombox-playing", netId, new() { ["plays"] = plays });
        }
        // Taken out (its object plays now), turned into a silhouette or gone: it stops.
        foreach (ushort netId in boomboxes.Keys.Where(netId => playing == null || !playing.Contains(netId)).ToArray())
        {
            boomboxes[netId].Stop();
            boomboxes.Remove(netId);
        }
    }

    /// <summary>The cassette in a stand-in's magazine (its nested ammo), or null.</summary>
    internal static string CassettePrefabOf(string standInState) =>
        StandInAmmo.Of(standInState).Select(entry => entry.PrefabName).FirstOrDefault(prefab => StashedCassette.CassetteOfPrefab(prefab) != null);

    /// <summary>
    /// Before a stand-in's item is taken out: its row takes the position its cassette has got to,
    /// so the object made from it resumes there, and the server's copy of the row with it.
    /// </summary>
    public static void BeforeTakeOut(InventoryStandIn standIn)
    {
        if (standIn != null && boomboxes.ContainsKey(standIn.NetId))
            KeepPosition(standIn.NetId, "take-out");
    }

    // The cassette's position goes into its row, and up to the server.
    private static void KeepPosition(ushort netId, string why)
    {
        if (!boomboxes.TryGetValue(netId, out StashedBoombox boombox) || !boombox.TryGetPosition(out int track, out long seek, out bool ranOut) ||
            !InventoryStandIn.TryGet(netId, out InventoryStandIn standIn) || Parse(standIn) is not { } state ||
            !StashedCassette.TryRead(state, out StashedCassette.Position was))
            return;
        StashedCassette.Position position = new(track, ranOut ? 0L : seek, was.Playing, ranOut);
        StashedCassette.Write(state, position);
        standIn.State = state.ToString(Newtonsoft.Json.Formatting.None);
        ItemClient.Current?.SendStashedCassette(netId, position.Track, position.Seek, position.Ended);
        Trace("item.stashed-cassette-position", netId, new()
        {
            ["why"] = why,
            ["track"] = position.Track,
            ["seek"] = position.Seek,
            ["ended"] = position.Ended
        });
    }

    private static void StopAll()
    {
        foreach (StashedBoombox boombox in boomboxes.Values)
            boombox.Stop();
        boomboxes.Clear();
    }

    // The music pauses with the game, as a boombox's own does (BoomboxInteractionController.OnGamePaused),
    // and so does what this game hears of other players' (HeardStashedSounds).
    private static void WatchPause()
    {
        AppUtil app = AppUtil.Instance;
        if (app == pauseSource)
            return;
        if (pauseSource != null)
        {
            pauseSource.GamePaused -= OnGamePaused;
            pauseSource.GameUnpaused -= OnGameUnpaused;
        }
        pauseSource = app;
        if (app == null)
            return;
        app.GamePaused += OnGamePaused;
        app.GameUnpaused += OnGameUnpaused;
    }

    private static void OnGamePaused()
    {
        foreach (StashedBoombox boombox in boomboxes.Values.Concat(HeardStashedSounds.Playing))
            boombox.OnGamePaused();
    }

    private static void OnGameUnpaused()
    {
        foreach (StashedBoombox boombox in boomboxes.Values.Concat(HeardStashedSounds.Playing))
            boombox.OnGameUnpaused();
    }

    // Where a stashed item's sounds come from: the player's camera, as ItemPositionHandler puts a
    // stashed item's.
    private static Transform SoundRoot
    {
        get
        {
            if (soundRoot == null)
            {
                GameObject root = new("[MP] Stashed item sounds");
                root.AddComponent<FollowCamera>();
                soundRoot = root.transform;
            }
            return soundRoot;
        }
    }

    private static void PlayOnce(AudioSource from)
    {
        if (from == null || from.clip == null)
            return;
        GameObject sound = new(from.name);
        sound.transform.SetParent(SoundRoot, false);
        CopySource(from, sound).Play();
        Object.Destroy(sound, from.clip.length + 0.5f);
    }

    /// <summary>
    /// A copy of a prefab's audio source on another object, set as the prefab sets it, and 2D as
    /// a stashed item's sources are (ItemAudioSpatialBlendController), unless it's
    /// <paramref name="spatial"/>: 3D, as the item's own are in the world.
    /// </summary>
    internal static AudioSource CopySource(AudioSource from, GameObject to, bool spatial = false)
    {
        AudioSource source = to.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.clip = from.clip;
        source.outputAudioMixerGroup = from.outputAudioMixerGroup;
        source.volume = from.volume;
        source.pitch = from.pitch;
        source.loop = from.loop;
        source.priority = from.priority;
        source.spatialBlend = spatial ? 1f : 0f;
        source.spread = from.spread;
        source.dopplerLevel = from.dopplerLevel;
        source.reverbZoneMix = from.reverbZoneMix;
        source.rolloffMode = from.rolloffMode;
        source.minDistance = from.minDistance;
        source.maxDistance = from.maxDistance;
        if (from.rolloffMode == AudioRolloffMode.Custom)
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
        source.bypassEffects = from.bypassEffects;
        source.bypassListenerEffects = from.bypassListenerEffects;
        source.bypassReverbZones = from.bypassReverbZones;
        source.ignoreListenerPause = from.ignoreListenerPause;
        return source;
    }

    private sealed class FollowCamera : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera camera = PlayerManager.ActiveCamera;
            if (camera != null)
                transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
        }
    }
}
