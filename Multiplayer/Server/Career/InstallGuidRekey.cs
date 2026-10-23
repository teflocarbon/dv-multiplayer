using System;
using System.Linq;
using DV.JObjectExtstensions;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;

namespace Multiplayer.Server.Career;

/// <summary>
/// Moves a player's state from the install GUID they used to be known by to their Steam-derived
/// player ID, once (docs/design/server-player-split.md, "Identity"). It happens the first time
/// they log in verified to a career that has data for their old GUID and none for their Steam ID.
///
/// It covers the save as loaded (Players, item records, the ledger, Lost and Found, cold containers), so a
/// system that imports later finds the new ID, and the state already imported (the item registry, the
/// ledger's owners, holders and starter kits, Lost and Found, the cold-container graph).
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
         LiveItems.HasIdentity(id) ||
         LostAndFoundServer.HasOwner(id) ||
         ContainerServer.Graph.HasOwner(id.ToString("D")) ||
         ServerLedger.Ledger.HasIdentity(id));

    /// <summary>Re-keys the save root and the imported state. Returns how many records changed.</summary>
    public static int Rekey(JObject root, Guid from, Guid to)
    {
        int changed = root != null ? RekeySave(root, from, to) : 0;
        changed += LiveItems.RekeyIdentity(from, to);
        changed += LostAndFoundServer.RekeyOwner(from, to);
        changed += ContainerServer.Graph.RekeyOwner(from.ToString("D"), to.ToString("D"));
        bool kit = ServerLedger.Ledger.HasStarterKit(from);
        changed += ServerLedger.Ledger.Rekey(from, to) + (kit ? 1 : 0);
        return changed;
    }

    public static bool SaveOwns(JObject root, Guid id)
    {
        if (root == null || id == Guid.Empty)
            return false;
        string key = id.ToString("D");
        if ((root[PlayersKey] as JObject)?.Properties().Any(p => SameId(p.Name, id)) == true)
            return true;
        if (Records(root, ItemSaveLoad.SaveKey)
            .Any(value => SameId(value["ownerIdentity"], id) || SameId(value["placementPlayerIdentity"], id)))
            return true;
        if (Records(root, LostAndFoundKey).Any(value => SameId(value["ownerIdentity"], id)))
            return true;
        if (LedgerItems(root).Any(value => LedgerFields(value).Any(field => SameId(field.Value, id))) ||
            LedgerPlayers(root)?.Properties().Any(p => SameId(p.Name, id)) == true)
            return true;
        ContainerGraphSnapshot snapshot = ColdContainers(root);
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

        foreach (JObject value in Records(root, ItemSaveLoad.SaveKey))
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

        // The ledger as saved, for a login before it has loaded: owners, holders, who caused each
        // entry, and the starter kit. Rows are edited in place, so one the ledger can't read stays.
        foreach (JObject value in LedgerItems(root))
        {
            bool touched = false;
            foreach (JProperty field in LedgerFields(value).Where(field => SameId(field.Value, from)).ToArray())
            {
                field.Value = target;
                touched = true;
            }
            if (touched)
                changed++;
        }
        if (LedgerPlayers(root) is JObject kits)
        {
            JProperty old = kits.Properties().FirstOrDefault(p => SameId(p.Name, from));
            if (old != null && !kits.Properties().Any(p => SameId(p.Name, to)))
            {
                old.Remove();
                kits[target] = old.Value;
                changed++;
            }
        }

        ContainerGraphSnapshot snapshot = ColdContainers(root);
        int cold = snapshot?.RekeyOwner(from.ToString("D"), target) ?? 0;
        if (cold > 0)
        {
            root[ContainerServer.SaveKey] = Convert.ToBase64String(ContainerSnapshotCodec.Encode(snapshot));
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

    private static System.Collections.Generic.IEnumerable<JObject> LedgerItems(JObject root) =>
        (root[ServerLedger.SaveKey]?["items"] as JArray ?? new JArray()).OfType<JObject>();

    private static JObject LedgerPlayers(JObject root) => root[ServerLedger.SaveKey]?["players"] as JObject;

    // The identities a saved ledger entry names (LedgerSaveCodec): its owner, its holder, who caused it.
    private static System.Collections.Generic.IEnumerable<JProperty> LedgerFields(JObject item) =>
        new[] { item.Property("owner"), (item["where"] as JObject)?.Property("holder"), (item["born"] as JObject)?.Property("by") }
            .Where(field => field != null);

    private static ContainerGraphSnapshot ColdContainers(JObject root)
    {
        string encoded = (string)root[ContainerServer.SaveKey];
        if (string.IsNullOrWhiteSpace(encoded))
            return null;
        try
        {
            return ContainerSnapshotCodec.TryDecode(Convert.FromBase64String(encoded),
                out ContainerGraphSnapshot snapshot, out _) ? snapshot : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
