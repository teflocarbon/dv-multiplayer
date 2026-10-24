using System.Collections.Generic;
using System.Linq;
using DV;
using DV.JObjectExtstensions;
using DV.TimeKeeping;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Debugging;
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
/// <item>a boombox left on in radio mode keeps playing its station at its volume. One with a
/// cassette stays an object until R5 (OI-CON-16), and plays as vanilla's does.</item>
/// </list>
/// A silhouette's item is elsewhere, so it does none of these: its object does.
/// </summary>
[HarmonyPatch]
internal static class StashedItems
{
    private const string PowerOnKey = "powerOn";
    private const string RadioModeKey = "radioMode";
    private const string VolumeKey = "vol";
    private const string StationKey = "rsIndex";

    private static readonly AlarmRows.Ticker<ushort> alarms = new();
    private static readonly Dictionary<ushort, StashedRadio> radios = new();
    private static readonly Dictionary<string, bool> boomboxPrefabs = new();
    // A radio follows its row a few times a second: a take-out stops it within this.
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

    /// <summary>Every frame on a game with a local player: alarms tick on the world's minute, and radios follow their rows.</summary>
    public static void Update()
    {
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
            UpdateRadios();
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

    private static void Trace(string eventName, ushort netId, Dictionary<string, object> data)
    {
        if (DebugRuntime.EnabledFor("item"))
            DebugRuntime.Publish("item", eventName, NetworkRole.DebugSide, entityType: "Item", entityId: netId.ToString(), data: data);
    }

    private static bool IsBoombox(string prefabName)
    {
        if (!boomboxPrefabs.TryGetValue(prefabName, out bool boombox))
        {
            InventoryItemSpec spec = InventoryStandIn.FindSpec(prefabName);
            boombox = spec != null && spec.GetComponent<BoomboxInteractionController>() != null;
            boomboxPrefabs[prefabName] = boombox;
        }
        return boombox;
    }

    private static void UpdateRadios()
    {
        HashSet<ushort> playing = null;
        foreach (InventoryStandIn standIn in Stashed)
        {
            if (!IsBoombox(standIn.PrefabName) || string.IsNullOrEmpty(standIn.State))
                continue;
            if (Parse(standIn) is not { } state || state.GetBool(PowerOnKey) != true || state.GetBool(RadioModeKey) != true)
                continue;
            (playing ??= new HashSet<ushort>()).Add(standIn.NetId);
            if (radios.ContainsKey(standIn.NetId))
                continue;
            StashedRadio radio = StashedRadio.Start(standIn, state, SoundRoot);
            if (radio != null)
                radios.Add(standIn.NetId, radio);
        }
        // Taken out (its object plays now), turned into a silhouette or gone: it stops.
        foreach (ushort netId in radios.Keys.Where(netId => playing == null || !playing.Contains(netId)).ToArray())
        {
            radios[netId].Stop();
            radios.Remove(netId);
        }
        WatchPause();
    }

    private static void StopAll()
    {
        foreach (StashedRadio radio in radios.Values)
            radio.Stop();
        radios.Clear();
    }

    // The radios pause with the game, as a boombox's own do (BoomboxInteractionController.OnGamePaused).
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
        foreach (StashedRadio radio in radios.Values)
            radio.Controller.OnGamePaused();
    }

    private static void OnGameUnpaused()
    {
        foreach (StashedRadio radio in radios.Values)
            radio.Controller.OnGameUnpaused();
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
    /// a stashed item's sources are (ItemAudioSpatialBlendController).
    /// </summary>
    internal static AudioSource CopySource(AudioSource from, GameObject to)
    {
        AudioSource source = to.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.clip = from.clip;
        source.outputAudioMixerGroup = from.outputAudioMixerGroup;
        source.volume = from.volume;
        source.pitch = from.pitch;
        source.loop = from.loop;
        source.priority = from.priority;
        source.spatialBlend = 0f;
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

    /// <summary>
    /// A stashed boombox's radio: vanilla's own radio player (<c>RadioPlayerController</c>) on a
    /// copy of the boombox's music source, tuned to the row's station at the row's volume. A
    /// stashed boombox's interference is paused (its controller is disabled), so a copy has none.
    /// </summary>
    private sealed class StashedRadio
    {
        private GameObject host;
        public RadioPlayerController Controller { get; private set; }

        public static StashedRadio Start(InventoryStandIn standIn, JObject state, Transform parent)
        {
            AudioSource music = standIn.Prefab.GetComponent<BoomboxInteractionController>()?.volumeAndInterferenceController?.musicAudioSource;
            if (music == null)
                return null;
            GameObject host = new($"[MP] Stashed radio {standIn.NetId}");
            host.transform.SetParent(parent, false);
            AudioSource source = CopySource(music, host);
            // As the boombox's own (BoomboxInteractionController.Awake).
            source.ignoreListenerPause = true;
            source.volume = Mathf.Clamp01(state.GetFloat(VolumeKey) ?? VolumeAndInterferenceController.DEFAULT_VOLUME);
            RadioPlayerController controller = new(host, source);
            controller.OverrideLastPlayedStationIndex(state.GetInt(StationKey) ?? 0);
            controller.OnPowerAndModeChanged(true, true);
            Trace("item.stashed-radio-playing", standIn.NetId, new()
            {
                ["station"] = state.GetInt(StationKey) ?? 0,
                ["volume"] = source.volume
            });
            return new StashedRadio { host = host, Controller = controller };
        }

        public void Stop()
        {
            Controller.TurnOff();
            if (host != null)
                Object.Destroy(host);
        }
    }
}
