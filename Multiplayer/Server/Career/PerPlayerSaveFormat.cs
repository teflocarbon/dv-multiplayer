using DV;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using Multiplayer.Server.Players;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Server.Career;

/// <summary>
/// The save format where every player's state is stored by their player ID (PlayerIdentity), the
/// host's own player included: a position under Multiplayer.Players.&lt;ID&gt;. Items are all in
/// Multiplayer.Items (docs/design/items/save-load.md). The server restores a player's state at their
/// login, wherever the server runs. See docs/design/server-player-split.md.
///
/// DV's own fields (Player_position, Storage_Inventory) are still written from the local player,
/// so a career opens in vanilla DV without the mod, but the mod never reads its item lists (D2), and
/// reads its position only to convert a career saved before this format, once.
///
/// Runs when hosting starts (SaveGameManager_FindStartGameData_Patch), after DV has read the save
/// and before it places the player or loads any item.
/// </summary>
public static class PerPlayerSaveFormat
{
    private const string RootKey = "Multiplayer";
    private const string PlayersKey = "Players";
    private const string FormatKey = "PlayerStateFormat";
    private const int Version = 2;

    /// <summary>The ID the local player logs in with: this game's Steam account's.</summary>
    private static Guid LocalId => PlayerIdentity.Local;

    /// <summary>
    /// Who a career saved before this format belongs to: the Steam account the game runs under, or
    /// -mp-owner, for a career copied to a server PC running under another account.
    /// </summary>
    private static Guid ConversionOwner => CareerOwner.Id;

    public static void PrepareLoad(SaveGameData data)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (data == null || !lifecycle.IsServerRunning)
            return;

        JObject root = data.GetJObject(RootKey) ?? new JObject();
        if (((int?)root[FormatKey] ?? 0) < Version)
            Convert(data, root, ConversionOwner);
        root[FormatKey] = Version;

        // The local player's state saved under the install's GUID, before player IDs were Steam
        // accounts, moves to their Steam ID once. Other players' moves at their login.
        Guid installGuid = Multiplayer.Settings.GetGuid();
        if (!lifecycle.IsDedicatedHost && installGuid != LocalId &&
            InstallGuidRekey.SaveOwns(root, installGuid) && !InstallGuidRekey.SaveOwns(root, LocalId))
            Multiplayer.Log($"Save format: moved {InstallGuidRekey.RekeySave(root, installGuid, LocalId)} record(s) " +
                            $"of the local player from install GUID {installGuid} to {PlayerIdentity.Describe(LocalId)}");
        data.SetJObject(RootKey, root);

        if (!lifecycle.IsDedicatedHost)
        {
            // The local player spawns where their ID was last, which needn't be where DV's own
            // copy says (a dedicated host's copy is its parked body).
            JObject self = (root[PlayersKey] as JObject)?.GetJObject(LocalId.ToString());
            Vector3? position = self?.GetVector3(SaveGameKeys.Player_position);
            if (position.HasValue)
            {
                data.SetVector3(SaveGameKeys.Player_position, position.Value);
                data.SetVector3(SaveGameKeys.Player_rotation, new Vector3(0f, self.GetFloat(SaveGameKeys.Player_rotation) ?? 0f, 0f));
                data.SetString(SaveGameKeys.Player_car_guid, string.Empty);
            }
        }

        // Vanilla's item lists are a mirror the mod writes and never reads (D2): every item comes from
        // Multiplayer.Items, so vanilla makes none of them. Installed gadgets stay vanilla's to make.
        data.SetObject(SaveGameKeys.Storage_Inventory, new List<StorageItemData>());
        data.SetObject(SaveGameKeys.Storage_World, new List<StorageItemData>());
        data.SetObject(SaveGameKeys.Storage_LostAndFound, new List<StorageItemData>());
        Multiplayer.Log($"Save format: per-player (local player {(lifecycle.IsDedicatedHost ? "is the dedicated host's parked body" : PlayerIdentity.Describe(LocalId))})");
    }

    // A career saved before this format: the host player's state is only in DV's fields. Move it to
    // the owner's ID, as if they had been a client all along. Other players' entries are already
    // per player (under install GUIDs, which InstallGuidRekey moves at their login). Runs once:
    // the marker is set after it. Its items aren't converted: a career saved before Multiplayer.Items
    // starts every player's items fresh (D1).
    private static void Convert(SaveGameData data, JObject root, Guid owner)
    {
        bool moved = ConvertSave(root, owner, data.GetVector3(SaveGameKeys.Player_position),
            data.GetVector3(SaveGameKeys.Player_rotation)?.y ?? 0f);
        Multiplayer.Log($"Save format: converted a career saved before per-player state; " +
                        (moved ? $"the position now belongs to {owner}" : $"{owner} already had a position"));
    }

    /// <summary>
    /// Moves the position DV keeps for the local player into the save's Multiplayer root, owned by
    /// <paramref name="owner"/>. An existing entry for the owner is kept, so converting the same root
    /// twice changes nothing the second time. Returns whether it made the entry.
    /// </summary>
    public static bool ConvertSave(JObject root, Guid owner, Vector3? position, float rotationY)
    {
        JObject players = root.GetJObject(PlayersKey) ?? new JObject();
        string key = owner.ToString();
        bool moved = players.GetJObject(key) == null;
        if (moved)
        {
            JObject entry = new JObject();
            if (position.HasValue)
                entry.SetVector3(SaveGameKeys.Player_position, position.Value);
            entry.SetFloat(SaveGameKeys.Player_rotation, rotationY);
            players.SetJObject(key, entry);
        }
        root.SetJObject(PlayersKey, players);
        return moved;
    }
}
