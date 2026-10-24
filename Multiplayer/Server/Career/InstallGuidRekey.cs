using Multiplayer.Core.Items.Ledger;
using System;
using System.Linq;
using DV.JObjectExtstensions;
using Multiplayer.Server.Items.Inventory;
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
/// they log in verified to a career that has data for their old GUID. If their Steam ID has data
/// too, the two are merged, never skipped (OI-ID-19): an item whose slot the Steam ID already uses
/// goes to their Lost and Found, so nothing is left under an identity no one logs in as.
///
/// It covers the save as loaded (Players and the <c>Items</c> key), the rows the load kept as they were,
/// and the state already loaded (the item registry, the
/// ledger's owners, holders and starter kits, Lost and Found, cold inventories; the cold-container
/// graph has no owners of its own, they are the ledger's).
/// </summary>
public static class InstallGuidRekey
{
    private const string RootKey = "Multiplayer";
    private const string PlayersKey = "Players";

    /// <summary>At login, before the player's records are bound to them.</summary>
    public static void OnLogin(ServerPlayer player)
    {
        if (player == null || player.LegacyGuid == Guid.Empty || player.LegacyGuid == player.Guid ||
            !PlayerIdentity.TryGetSteamId(player.Guid, out _))
            return;
        MoveInto(player.LegacyGuid, player.Guid, player.Username);
    }

    /// <summary>Re-keys from one ID to another, merging into what the second has, if the first has any data.</summary>
    public static bool MoveInto(Guid from, Guid to, string who)
    {
        SaveGameData data = SaveGameManager.Instance?.data;
        JObject root = data?.GetJObject(RootKey);
        if (from == to || !Owns(root, from))
            return false;

        bool merging = Owns(root, to);
        int changed = Rekey(root, from, to);
        if (root != null)
            data.SetJObject(RootKey, root);
        Multiplayer.Log($"{who}: moved {changed} record(s) saved under install GUID {from} to {PlayerIdentity.Describe(to)}" +
                        (merging ? ", merged with what it had" : string.Empty));
        return true;
    }

    /// <summary>Whether the save, or the state imported from it, has anything of this ID's.</summary>
    public static bool Owns(JObject root, Guid id) =>
        id != Guid.Empty &&
        (SaveOwns(root, id) ||
         LiveItems.HasIdentity(id) ||
         ColdInventories.HasIdentity(id) ||
         LostAndFoundServer.HasOwner(id) ||
         ItemSaveLoad.KeptNames(id) ||
         ServerLedger.Ledger.HasIdentity(id));

    /// <summary>Re-keys the save root and the imported state. Returns how many records changed.</summary>
    public static int Rekey(JObject root, Guid from, Guid to)
    {
        int changed = root != null ? RekeySave(root, from, to) : 0;
        changed += LiveItems.RekeyIdentity(from, to);
        changed += LostAndFoundServer.RekeyOwner(from, to);
        bool kit = ServerLedger.Ledger.HasStarterKit(from);
        RekeyResult merged = ServerLedger.Ledger.Rekey(from, to);
        changed += merged.Changed + (kit ? 1 : 0);
        foreach (Guid id in merged.ToLostAndFound)
            Multiplayer.LogWarning($"Re-key {from:D} -> {to:D}: item {id:D}'s slot was taken, so the ledger moved it to Lost and Found");
        // The cold inventory's rows, after the ledger so its slot check sees the new identity's rows
        // where they are; one the ledger sent to Lost and Found goes there when the player joins.
        changed += ColdInventories.Rekey(from, to, merged.ToLostAndFound);
        // Rows the load kept as they were (unloadable, unreadable): written back with the new identity.
        changed += ItemSaveLoad.RekeyKept(from, to);
        return changed;
    }

    public static bool SaveOwns(JObject root, Guid id)
    {
        if (root == null || id == Guid.Empty)
            return false;
        if ((root[PlayersKey] as JObject)?.Properties().Any(p => SameId(p.Name, id)) == true)
            return true;
        return TokenNames(root[ItemsSaveCodec.Key], id);
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

        // The items as saved, for a login before they have loaded: every identity in them (owners,
        // holders, who caused an entry, an earlier build's container owner, a starter kit). Rows are edited in
        // place, so one the load can't read stays as it was.
        return changed + RekeyToken(root[ItemsSaveCodec.Key], from, to);
    }

    /// <summary>
    /// Replaces one identity with another everywhere in a saved token: string values, and property
    /// names (a player's entry), unless the new identity has one already. Returns how many changed.
    /// </summary>
    internal static int RekeyToken(JToken token, Guid from, Guid to)
    {
        int changed = 0;
        switch (token)
        {
            case JObject value:
                foreach (JProperty property in value.Properties().ToArray())
                {
                    changed += RekeyToken(property.Value, from, to);
                    if (!SameId(property.Name, from) || value.Properties().Any(p => SameId(p.Name, to)))
                        continue;
                    property.Remove();
                    value[to.ToString("D")] = property.Value;
                    changed++;
                }
                break;
            case JArray values:
                foreach (JToken value in values)
                    changed += RekeyToken(value, from, to);
                break;
            case JValue value when SameId(value, from):
                value.Value = to.ToString("D");
                changed++;
                break;
        }
        return changed;
    }

    /// <summary>Whether a saved token names this identity anywhere: a value or a property name.</summary>
    internal static bool TokenNames(JToken token, Guid id) => token switch
    {
        JObject value => value.Properties().Any(p => SameId(p.Name, id) || TokenNames(p.Value, id)),
        JArray values => values.Any(value => TokenNames(value, id)),
        JValue value => SameId(value, id),
        _ => false
    };

    private static bool SameId(JToken value, Guid id) =>
        value?.Type == JTokenType.String && SameId((string)value, id);

    private static bool SameId(string value, Guid id) =>
        Guid.TryParse(value, out Guid parsed) && parsed == id;
}
