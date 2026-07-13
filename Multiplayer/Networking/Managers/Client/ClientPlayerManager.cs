using DV;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Networking.Data.Player;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;

namespace Multiplayer.Networking.Managers.Client;

public class ClientPlayerManager
{
    private readonly Dictionary<byte, NetworkedPlayer> playerMap = new();

    public Action<NetworkedPlayer> OnPlayerConnected;
    public Action<NetworkedPlayer> OnPlayerDisconnected;
    public Action<NetworkedPlayer> OnPlayerPrefsUpdated;
    public IReadOnlyCollection<NetworkedPlayer> Players => playerMap.Values;

    private readonly GameObject playerTagPrefab;

    public ClientPlayerManager()
    {
        playerTagPrefab = Multiplayer.AssetIndex.PlayerTag;
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

        // Setup player tag
        Object.Instantiate(playerTagPrefab, go.transform);
        NetworkedPlayer networkedPlayer = go.AddComponent<NetworkedPlayer>();

        networkedPlayer.PlayerId = playerId;
        networkedPlayer.Username = username;
        networkedPlayer.CrewName = crewName;
        networkedPlayer.IsVR = isVr;

        // Get player model from registry and apply it to the player
        var model = Multiplayer.PlayerModelRegistry.GetModelById(characterId);

        networkedPlayer.ChangeModel(model.Prefab);

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
        Object.Destroy(networkedPlayer.gameObject);
        playerMap.Remove(playerid);
    }

    public void UpdatePing(byte playerId, int ping)
    {
        if (!TryGetPlayer(playerId, out NetworkedPlayer player))
            return;
        player.SetPing(ping);
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
            var model = Multiplayer.PlayerModelRegistry.GetModelById(characterId);
            player.ChangeModel(model.Prefab);
        }

        OnPlayerPrefsUpdated?.Invoke(player);
    }
}
