using System.Collections.Generic;
using System.Linq;
using Multiplayer.Shared.Players;
using Multiplayer.Shared.Protocol.Packets.Clientbound.World;
using Multiplayer.Shared.Session;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// Other players' stashed boomboxes, heard on this game (docs/design/items/inventory.md, "Stashed
/// items that act"; everything heard in the world is shared). A boombox in a player's inventory is
/// a row with no object on any game; its holder's game plays it at their camera
/// (<see cref="StashedItems"/>), and the server tells the players near them what it plays
/// (<see cref="ClientboundStashedSoundPacket"/>), so each of their games plays it at the holder's
/// avatar, in the world, as the boombox in their hands would sound.
/// </summary>
internal static class HeardStashedSounds
{
    // Where a carried boombox is, on its holder's avatar: about their hip.
    private static readonly Vector3 CarriedAt = new(0f, 1f, 0f);

    private sealed class Heard
    {
        public byte Holder;
        public StashedBoombox Boombox;
        public int Track = -1;
    }

    private static readonly Dictionary<ushort, Heard> heard = new();
    private static readonly Dictionary<byte, Transform> anchors = new();

    public static IEnumerable<StashedBoombox> Playing => heard.Values.Select(sound => sound.Boombox);

    /// <summary>What a nearby player's stashed boombox plays now, or that it stopped (no state).</summary>
    public static void Receive(ClientboundStashedSoundPacket packet)
    {
        if (packet == null || packet.NetId == 0)
            return;
        JObject state = Parse(packet.State);
        string plays = StashedBoombox.WhatPlays(state, packet.CassettePrefab);
        if (plays.Length == 0)
        {
            Stop(packet.NetId, "stopped");
            return;
        }
        StashedCassette.TryRead(state, out StashedCassette.Position position);
        int track = plays.StartsWith("cassette:") ? position.Track : -1;
        if (heard.TryGetValue(packet.NetId, out Heard running) && running.Holder == packet.HolderId &&
            running.Boombox.Plays == plays && running.Track == track)
            return;
        Stop(packet.NetId, "changed");
        if (AnchorOf(packet.HolderId) is not { } anchor)
            return;
        StashedBoombox boombox = StashedBoombox.Start($"{packet.NetId} of P{packet.HolderId}", packet.PrefabName, state, packet.CassettePrefab,
            anchor, true, packet.LateMilliseconds / 1000f);
        if (boombox == null)
            return;
        heard[packet.NetId] = new Heard { Holder = packet.HolderId, Boombox = boombox, Track = track };
        StashedItems.Trace("item.heard-stashed-boombox", packet.NetId, new()
        {
            ["holder"] = packet.HolderId,
            ["plays"] = plays,
            ["track"] = track,
            ["lateMs"] = packet.LateMilliseconds
        });
    }

    /// <summary>Every frame: a holder who left (their avatar gone) takes their music with them.</summary>
    public static void Update()
    {
        if (heard.Count == 0)
            return;
        foreach (KeyValuePair<ushort, Heard> sound in heard.ToArray())
            if (AnchorOf(sound.Value.Holder) == null)
                Stop(sound.Key, "holder-gone");
    }

    private static void Stop(ushort netId, string why)
    {
        if (!heard.TryGetValue(netId, out Heard sound))
            return;
        sound.Boombox.Stop();
        heard.Remove(netId);
        StashedItems.Trace("item.heard-stashed-boombox-stopped", netId, new() { ["why"] = why });
    }

    private static Transform AnchorOf(byte holder)
    {
        if (anchors.TryGetValue(holder, out Transform anchor) && anchor != null)
            return anchor;
        anchors.Remove(holder);
        PlayerAvatars avatars = NetworkLifecycle.Instance?.Avatars;
        if (avatars == null || !avatars.TryGetPlayer(holder, out NetworkedPlayer player) || player == null)
            return null;
        anchor = new GameObject($"[MP] Stashed sounds of P{holder}").transform;
        anchor.SetParent(player.transform, false);
        anchor.localPosition = CarriedAt;
        anchors[holder] = anchor;
        return anchor;
    }

    private static JObject Parse(string state)
    {
        if (string.IsNullOrEmpty(state))
            return null;
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
