using System.Linq;
using DV.JObjectExtstensions;
using DV.Radio;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// Where a boombox's cassette has got to, kept in its state (docs/design/items/inventory.md,
/// "Stashed items that act"; OI-CON-16). Vanilla saves the track (<c>cpIndex</c>) and whether it
/// plays (<c>cassettePlaying</c>), and a load starts that track from its beginning. A boombox that
/// is a row keeps playing (<see cref="StashedItems"/>), so its row also keeps the position in the
/// track (<see cref="SeekKey"/>, in samples, as vanilla's player counts them) and whether the tape
/// ran out (<see cref="EndedKey"/>), and its object made again resumes there
/// (<see cref="BoomboxStatePatch"/>).
/// </summary>
internal static class StashedCassette
{
    public const string TrackKey = "cpIndex";
    public const string PlayingKey = "cassettePlaying";
    public const string SeekKey = "mpCassetteSeek";
    public const string EndedKey = "mpCassetteEnded";

    private static readonly AccessTools.FieldRef<PlaylistPlayer, BasePlayer> filePlayer =
        AccessTools.FieldRefAccess<PlaylistPlayer, BasePlayer>("player");

    public readonly struct Position
    {
        public readonly int Track;
        public readonly long Seek;
        public readonly bool Playing;
        public readonly bool Ended;

        public Position(int track, long seek, bool playing, bool ended)
        {
            Track = track;
            Seek = seek;
            Playing = playing;
            Ended = ended;
        }
    }

    /// <summary>The position a state names. No track: none (a boombox with no cassette).</summary>
    public static bool TryRead(JObject state, out Position position)
    {
        int? track = state?.GetInt(TrackKey);
        position = track.HasValue
            ? new Position(Mathf.Max(0, track.Value), state[SeekKey] is { Type: JTokenType.Integer } seek ? System.Math.Max(0L, (long)seek) : 0L,
                state.GetBool(PlayingKey) == true, state.GetBool(EndedKey) == true)
            : default;
        return track.HasValue;
    }

    /// <summary>A state with this position written into it.</summary>
    public static void Write(JObject state, Position position)
    {
        state[TrackKey] = position.Track;
        state[PlayingKey] = position.Playing;
        state[SeekKey] = position.Seek;
        if (position.Ended)
            state[EndedKey] = true;
        else
            state.Remove(EndedKey);
    }

    /// <summary>
    /// A boombox's state as a stand-in keeps it: with its cassette's position in the track, which
    /// vanilla's save leaves out. Any other item's state is as it was.
    /// </summary>
    public static string WithPosition(GameObject tool, string state)
    {
        BoomboxInteractionController boombox = tool != null ? tool.GetComponent<BoomboxInteractionController>() : null;
        if (boombox == null || string.IsNullOrEmpty(state) || Parse(state) is not { } parsed)
            return state;
        parsed.Remove(SeekKey);
        parsed.Remove(EndedKey);
        if (TryRead(parsed, out Position saved) && boombox.cassetteInteractionAreaScript?.GetInsertedCassette() is { } cassette)
        {
            PlaylistPlayer player = boombox.cassetteController?.player;
            long seek = player != null ? player.GetSeekPosition() : cassette.lastPlayedSeekPosition;
            // The tape ran out: vanilla's player stays "playing" with nothing left to play.
            bool ended = saved.Playing && player != null && filePlayer(player) == null;
            Write(parsed, new Position(saved.Track, ended ? 0L : seek, saved.Playing, ended));
        }
        return parsed.ToString(Formatting.None);
    }

    /// <summary>The cassette in a stand-in's state (its nested ammo), or null.</summary>
    public static Cassette CassetteOf(string standInState) =>
        StandInAmmo.Of(standInState).Select(entry => CassetteOfPrefab(entry.PrefabName)).FirstOrDefault(cassette => cassette != null);

    /// <summary>A cassette prefab's own <see cref="Cassette"/>, which names its playlist, or null.</summary>
    public static Cassette CassetteOfPrefab(string prefabName) =>
        string.IsNullOrEmpty(prefabName) ? null : InventoryStandIn.FindSpec(prefabName)?.GetComponent<Cassette>();

    /// <summary>Whether the cassette's player has run out of tape (its playlist ended).</summary>
    public static bool RanOut(PlaylistPlayer player) => player != null && filePlayer(player) == null;

    private static JObject Parse(string state)
    {
        try
        {
            return JObject.Parse(state);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
