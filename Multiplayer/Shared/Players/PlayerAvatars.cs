using DV;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Protocol.Data.Player;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Shared.Players;

/// <summary>
///     The other players' avatars in this game's world (<see cref="NetworkedPlayer"/>), reached as
///     <c>NetworkLifecycle.Instance.Avatars</c> on every game in a session, a dedicated host's
///     included: the server needs them too (held items hang on their hands, plugs follow them,
///     cars count who is aboard). A remote client fills it from the server's packets; on a host,
///     whoever follows the other players does (docs/design/host-local-session.md, stage 3).
/// </summary>
public class PlayerAvatars
{
    private readonly Dictionary<byte, NetworkedPlayer> playerMap = new();

    public Action<NetworkedPlayer> OnPlayerConnected;
    public Action<NetworkedPlayer> OnPlayerDisconnected;
    public Action<NetworkedPlayer> OnPlayerPrefsUpdated;
    public IReadOnlyCollection<NetworkedPlayer> Players => playerMap.Values;

    private readonly IAvatarPresentation presentation;

    public PlayerAvatars(IAvatarPresentation presentation)
    {
        this.presentation = presentation;

        // A static method, so re-subscribing for every session replaces instead of piling up.
        WorldStreamingInit.LoadingFinished -= NetworkedPlayer.CaptureItemAnchorOffset;
        WorldStreamingInit.LoadingFinished += NetworkedPlayer.CaptureItemAnchorOffset;
    }

    public bool TryGetPlayer(byte playerid, out NetworkedPlayer player)
    {
        return playerMap.TryGetValue(playerid, out player);
    }

    public void AddPlayer(byte playerId, string username, string crewName, string characterId, bool isVr)
    {
        if (playerMap.ContainsKey(playerId))
        {
            Multiplayer.LogWarning($"Player with id {playerId} already exists. Removing existing player {playerMap[playerId].Username}");
            RemovePlayer(playerId);
        }

        // Player model holder
        GameObject go = new($"Player[{username}]");
        go.transform.SetParent(WorldMover.OriginShiftParent);
        go.layer = LayerMask.NameToLayer(Layers.Player);

        NetworkedPlayer networkedPlayer = go.AddComponent<NetworkedPlayer>();
        presentation.AddNameTag(networkedPlayer);

        networkedPlayer.PlayerId = playerId;
        networkedPlayer.Username = username;
        networkedPlayer.CrewName = crewName;
        networkedPlayer.IsVR = isVr;

        presentation.ChangeModel(networkedPlayer, characterId);

        playerMap.Add(playerId, networkedPlayer);
        EntityDebugRegistry.RegisterPlayer(networkedPlayer);
        DebugRuntime.Publish("player", "player.joined", DebugRuntimeSide.Client, entityType: "Player", entityId: playerId.ToString(), data: DebugValueSnapshotter.SnapshotObject(new { username, crewName, characterId, isVr }));
        OnPlayerConnected?.Invoke(networkedPlayer);
    }

    public void RemovePlayer(byte playerid)
    {
        if (!TryGetPlayer(playerid, out NetworkedPlayer networkedPlayer))
            return;

        OnPlayerDisconnected?.Invoke(networkedPlayer);
        DebugRuntime.Publish("player", "player.disconnected", DebugRuntimeSide.Client, entityType: "Player", entityId: playerid.ToString(), data: EntityDebugRegistry.Get("Player", playerid.ToString())?.LatestState);
        EntityDebugRegistry.Unregister("Player", playerid.ToString());
        // Held and inventory items are children of the avatar. Destroying it with them attached
        // destroyed the leaving player's items for everyone (the host treats it as a real destroy).
        networkedPlayer.ReleaseAllItems();
        Object.Destroy(networkedPlayer.gameObject);
        playerMap.Remove(playerid);
    }

    public void UpdatePing(byte playerId, int ping)
    {
        if (!TryGetPlayer(playerId, out NetworkedPlayer player))
            return;
        presentation.SetPing(player, ping);
    }

    public void UpdatePosition(byte playerId, PlayerTrackingData trackingData, PlayerPostureFlags posture, bool isOnCar, ushort carId)
    {
        if (!TryGetPlayer(playerId, out NetworkedPlayer player))
            return;
        player.UpdateCar(carId);
        player.UpdatePosition(trackingData, posture, isOnCar);
        EntityDebugRegistry.RegisterPlayer(player);
    }

    public void UpdatePreferences(byte playerId, Dictionary<PlayerPreference, string> preferences)
    {
        Multiplayer.LogDebug(() => $"Updating preferences for playerId: {playerId}, Preference count : {preferences?.Count}");

        if (!TryGetPlayer(playerId, out NetworkedPlayer player))
            return;

        if (preferences.TryGetValue(PlayerPreference.CrewName, out string crewName))
            player.CrewName = crewName;

        if (preferences.TryGetValue(PlayerPreference.CharacterModel, out string characterId))
        {
            presentation.ChangeModel(player, characterId);
        }

        OnPlayerPrefsUpdated?.Invoke(player);
    }
}
