using System.Collections.Generic;
using System.Linq;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Session;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.Inventory;

internal static partial class PlayerItems
{
    // A stashed boombox is heard by the players near its holder (docs/design/items/inventory.md,
    // "Stashed items that act"; everything heard in the world is shared). Its holder's game plays
    // it from its stand-in (StashedItems), and the server reads what it plays from its row (power,
    // mode, station, volume, the cassette and where it had got to). Each player within the
    // boombox's hearing is told as it starts, changes or stops, or as they come near or go
    // (ClientboundStashedSoundPacket), and their game plays it at the holder's avatar
    // (HeardStashedSounds). The holder's game keeps the row's cassette position
    // (KeepCassettePosition) as it moves to another track, runs out or is taken out, so whoever
    // starts it later starts there, and the object made from the row resumes there.

    private const float SoundCheckSeconds = 0.5f;
    // Heard a little past where the boombox's own source fades out, so it's playing as it comes in.
    private const float HearingMargin = 10f;
    private const float DefaultHearing = 50f;

    private sealed class Sound
    {
        public byte Holder;
        // What the players near it were last told: the holder, the cassette, the row's state.
        public string Told = string.Empty;
        // When the row's cassette position was where the cassette had got to.
        public float PositionSince;
        public readonly HashSet<byte> Hearing = new();
    }

    private static readonly Dictionary<ushort, Sound> sounds = new();
    private static readonly Dictionary<string, float> hearing = new();
    private static float nextSoundCheck;

    private static void ClearSounds()
    {
        sounds.Clear();
        nextSoundCheck = 0f;
    }

    /// <summary>
    /// Where a holder's stashed boombox's cassette has got to, from their game: kept in its row.
    /// Dropped for an item that isn't a boombox kept as data in their inventory.
    /// </summary>
    public static void KeepCassettePosition(ServerPlayer holder, ushort netId, int track, long seek, bool ended)
    {
        if (holder == null || !LiveItems.TryGet(netId, out LiveItems.LiveItem record) || record.Holder != holder.Guid ||
            !IsDataOnly(record) || !StashedItems.IsBoombox(record.PrefabName) ||
            !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row) || row.State == null ||
            !StashedCassette.TryRead(row.State, out StashedCassette.Position was))
            return;
        StashedCassette.Write(row.State, new StashedCassette.Position(Mathf.Max(0, track), ended ? 0L : System.Math.Max(0L, seek), was.Playing, ended));
        if (sounds.TryGetValue(netId, out Sound sound))
            sound.PositionSince = Time.realtimeSinceStartup;
        // The players near it hear the change now, not at the next check.
        nextSoundCheck = 0f;
    }

    /// <summary>
    /// The host's own take-out: its stand-in's row has the position its cassette had got to
    /// (StashedItems.BeforeTakeOut), and the server's row takes it in this frame, before the
    /// object is made from it.
    /// </summary>
    internal static void KeepCassettePosition(ServerPlayer host, InventoryStandIn standIn)
    {
        if (standIn == null || string.IsNullOrEmpty(standIn.State))
            return;
        JObject state;
        try
        {
            state = JObject.Parse(standIn.State);
        }
        catch (JsonException)
        {
            return;
        }
        if (StashedCassette.TryRead(state, out StashedCassette.Position position))
            KeepCassettePosition(host, standIn.NetId, position.Track, position.Seek, position.Ended);
    }

    /// <summary>Host, every frame: the players near each stashed boombox that plays are told what it plays.</summary>
    public static void UpdateSounds()
    {
        float now = Time.realtimeSinceStartup;
        NetworkServer server = NetworkServer.Instance;
        if (now < nextSoundCheck || server == null)
            return;
        nextSoundCheck = now + SoundCheckSeconds;
        HashSet<ushort> playing = new();
        foreach (LiveItems.LiveItem record in LiveItems.Records)
        {
            if (!StashedItems.IsBoombox(record.PrefabName) || !IsDataOnly(record) ||
                !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row) || row.State == null)
                continue;
            ServerPlayer holder = server.ServerPlayers.FirstOrDefault(player => player.Guid == record.Holder);
            string cassette = DataAmmoOf(record.NetId).Select(ammo => ammo.PrefabName)
                .FirstOrDefault(prefab => StashedCassette.CassetteOfPrefab(prefab) != null) ?? string.Empty;
            if (holder == null || StashedBoombox.WhatPlays(row.State, cassette).Length == 0)
                continue;
            playing.Add(record.NetId);
            if (!sounds.TryGetValue(record.NetId, out Sound sound))
                sounds[record.NetId] = sound = new Sound { PositionSince = now };
            string state = row.State.ToString(Formatting.None);
            string told = $"{holder.PlayerId}|{cassette}|{state}";
            bool changed = sound.Told != told;
            if (changed && sound.Holder != holder.PlayerId)
                StopHearing(server, record.NetId, sound);
            sound.Told = told;
            sound.Holder = holder.PlayerId;
            float range = HearingOf(record.PrefabName);
            foreach (ServerPlayer other in server.ServerPlayers)
            {
                bool near = other != holder && other.LoadingState >= PlayerLoadingState.ReadyForItems &&
                            (other.WorldPosition - holder.WorldPosition).sqrMagnitude <= range * range;
                if (near && (changed || !sound.Hearing.Contains(other.PlayerId)))
                {
                    server.SendStashedSound(other, new ClientboundStashedSoundPacket
                    {
                        HolderId = holder.PlayerId,
                        NetId = record.NetId,
                        PrefabName = record.PrefabName,
                        CassettePrefab = cassette,
                        State = state,
                        LateMilliseconds = Mathf.RoundToInt((now - sound.PositionSince) * 1000f)
                    });
                    sound.Hearing.Add(other.PlayerId);
                }
                else if (!near && sound.Hearing.Remove(other.PlayerId))
                    server.SendStashedSound(other, Stopped(record.NetId, sound.Holder));
            }
        }
        // Taken out (its object plays now), turned off, run out, gone, or its holder left: it stops.
        foreach (KeyValuePair<ushort, Sound> stopped in sounds.Where(sound => !playing.Contains(sound.Key)).ToArray())
        {
            StopHearing(server, stopped.Key, stopped.Value);
            sounds.Remove(stopped.Key);
        }
    }

    private static void StopHearing(NetworkServer server, ushort netId, Sound sound)
    {
        foreach (byte playerId in sound.Hearing)
            if (server.ServerPlayers.FirstOrDefault(player => player.PlayerId == playerId) is { } player)
                server.SendStashedSound(player, Stopped(netId, sound.Holder));
        sound.Hearing.Clear();
    }

    private static ClientboundStashedSoundPacket Stopped(ushort netId, byte holder) => new() { HolderId = holder, NetId = netId };

    // How far a boombox is heard: as far as its music source reaches.
    private static float HearingOf(string prefabName)
    {
        if (!hearing.TryGetValue(prefabName, out float range))
        {
            AudioSource music = InventoryStandIn.FindSpec(prefabName)?.GetComponent<BoomboxInteractionController>()?
                .volumeAndInterferenceController?.musicAudioSource;
            hearing[prefabName] = range = (music != null ? music.maxDistance : DefaultHearing) + HearingMargin;
        }
        return range;
    }
}
