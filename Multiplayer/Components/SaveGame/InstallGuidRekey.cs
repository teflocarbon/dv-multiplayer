using System;
using System.Linq;
using DV.JObjectExtstensions;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Components.Networking.World.Containers;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Core.Containers;
using Multiplayer.Networking.Data;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Components.SaveGame;

/// <summary>
/// Moves a player's state from the install GUID they used to be known by to their Steam-derived
/// player ID, once (docs/design/server-player-split.md, "Identity"). It happens the first time
/// they log in verified to a career that has data for their old GUID and none for their Steam ID.
///
/// It covers the save as loaded (Players, item records, Lost and Found, cold containers), so a
/// system that imports later finds the new ID, and the state already imported (the item registry,
/// Lost and Found, the cold-container graph, starter kits given).
/// </summary>
public static class InstallGuidRekey
{
    private const string RootKey = "Multiplayer";
    private const string PlayersKey = "Players";
    private const string LostAndFoundKey = "LostAndFound";

    /// <summary>At login, before the player's records are bound to them.</summary>
    public static void OnLogin(ServerPlayer player)
    {
        if (player == null || player.LegacyGuid == Guid.Empty || player.LegacyGuid == player.Guid ||
            !PlayerIdentity.TryGetSteamId(player.Guid, out _))
            return;
        RekeyIfUnclaimed(player.LegacyGuid, player.Guid, player.Username);
    }

    /// <summary>Re-keys from one ID to another if the save has data for the first and none for the second.</summary>
    public static bool RekeyIfUnclaimed(Guid from, Guid to, string who)
    {
        SaveGameData data = SaveGameManager.Instance?.data;
        JObject root = data?.GetJObject(RootKey);
        if (!Owns(root, from) || Owns(root, to))
            return false;

        int changed = Rekey(root, from, to);
        if (root != null)
            data.SetJObject(RootKey, root);
        Multiplayer.Log($"{who}: moved {changed} record(s) saved under install GUID {from} to {PlayerIdentity.Describe(to)}");
        return true;
    }

    /// <summary>Whether the save, or the state imported from it, has anything of this ID's.</summary>
    public static bool Owns(JObject root, Guid id) =>
        id != Guid.Empty &&
        (SaveOwns(root, id) ||
         AuthoritativeItemRegistry.HasIdentity(id) ||
         NetworkedLostAndFoundManager.HasOwner(id) ||
         NetworkedColdContainerManager.Graph.HasOwner(id.ToString("D")) ||
         NetworkLifecycle.Instance?.Server?.StarterKitsGiven.Contains(id) == true);

    /// <summary>Re-keys the save root and the imported state. Returns how many records changed.</summary>
    public static int Rekey(JObject root, Guid from, Guid to)
    {
        int changed = root != null ? RekeySave(root, from, to) : 0;
        changed += AuthoritativeItemRegistry.RekeyIdentity(from, to);
        changed += NetworkedLostAndFoundManager.RekeyOwner(from, to);
        changed += NetworkedColdContainerManager.Graph.RekeyOwner(from.ToString("D"), to.ToString("D"));
        var kits = NetworkLifecycle.Instance?.Server?.StarterKitsGiven;
        if (kits != null && kits.Remove(from))
        {
            kits.Add(to);
            changed++;
        }
        return changed;
    }

    public static bool SaveOwns(JObject root, Guid id)
    {
        if (root == null || id == Guid.Empty)
            return false;
        string key = id.ToString("D");
        if ((root[PlayersKey] as JObject)?.Properties().Any(p => SameId(p.Name, id)) == true)
            return true;
        if (Records(root, WorldItemPersistenceManager.SaveKey)
            .Any(value => SameId(value["ownerIdentity"], id) || SameId(value["placementPlayerIdentity"], id)))
            return true;
        if (Records(root, LostAndFoundKey).Any(value => SameId(value["ownerIdentity"], id)))
            return true;
        ColdContainerGraphSnapshot snapshot = ColdContainers(root);
        return snapshot != null &&
               (snapshot.Containers.Any(container => container.OwnerIdentity == key) ||
                snapshot.Items.Any(item => item.PersistentOwnerIdentity == key));
    }

    /// <summary>Re-keys a save's Multiplayer root in place. Returns how many records changed.</summary>
    public static int RekeySave(JObject root, Guid from, Guid to)
    {
        int changed = 0;
        string target = to.ToString("D");

        if (root[PlayersKey] is JObject players)
        {
            JProperty old = players.Properties().FirstOrDefault(p => SameId(p.Name, from));
            if (old != null && !players.Properties().Any(p => SameId(p.Name, to)))
            {
                old.Remove();
                players[target] = old.Value;
                changed++;
            }
        }

        foreach (JObject value in Records(root, WorldItemPersistenceManager.SaveKey))
        {
            bool touched = false;
            foreach (string field in new[] { "ownerIdentity", "placementPlayerIdentity" })
            {
                if (!SameId(value[field], from))
                    continue;
                value[field] = target;
                touched = true;
            }
            if (touched)
                changed++;
        }

        foreach (JObject value in Records(root, LostAndFoundKey).Where(value => SameId(value["ownerIdentity"], from)))
        {
            value["ownerIdentity"] = target;
            changed++;
        }

        ColdContainerGraphSnapshot snapshot = ColdContainers(root);
        int cold = snapshot?.RekeyOwner(from.ToString("D"), target) ?? 0;
        if (cold > 0)
        {
            root[NetworkedColdContainerManager.SaveKey] = Convert.ToBase64String(ColdContainerSnapshotCodec.Encode(snapshot));
            changed += cold;
        }

        return changed;
    }

    private static bool SameId(JToken value, Guid id) =>
        value?.Type == JTokenType.String && SameId((string)value, id);

    private static bool SameId(string value, Guid id) =>
        Guid.TryParse(value, out Guid parsed) && parsed == id;

    private static System.Collections.Generic.IEnumerable<JObject> Records(JObject root, string key) =>
        (root[key] as JArray ?? new JArray()).OfType<JObject>();

    private static ColdContainerGraphSnapshot ColdContainers(JObject root)
    {
        string encoded = (string)root[NetworkedColdContainerManager.SaveKey];
        if (string.IsNullOrWhiteSpace(encoded))
            return null;
        try
        {
            return ColdContainerSnapshotCodec.TryDecode(Convert.FromBase64String(encoded),
                out ColdContainerGraphSnapshot snapshot, out _) ? snapshot : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
