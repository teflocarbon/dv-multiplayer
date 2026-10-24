using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// The sections of <c>Multiplayer.Items</c> (docs/design/items/save-load.md, "The key"), each row as
/// saved. Rows stand alone: one that can't be read is kept here as it was, and written back.
/// </summary>
public sealed class ItemsSave
{
    /// <summary>The ledger's entries that could be read.</summary>
    public List<LedgerEntry> Ledger { get; } = new();
    /// <summary>Entries written back from their row as saved, by id: on reading, every readable one; on writing, the unloadable ones (D9).</summary>
    public Dictionary<Guid, JObject> LedgerRows { get; } = new();
    /// <summary>Owned items' rows (pose and state), by id.</summary>
    public Dictionary<Guid, JObject> States { get; } = new();
    /// <summary>Unowned items' rows, each with its home. A row the loader can't read stays as it is.</summary>
    public List<JToken> Unowned { get; } = new();
    public List<JToken> Authored { get; } = new();
    public List<JToken> Containers { get; } = new();
    /// <summary>Lost and Found entries (reason, time, name), by id.</summary>
    public Dictionary<Guid, JObject> LostAndFound { get; } = new();
    public List<Guid> StarterKits { get; } = new();
    /// <summary>Rows that couldn't be read at all, by section, kept verbatim.</summary>
    public List<JToken> UnreadableLedger { get; } = new();
    public Dictionary<string, JToken> UnreadableStates { get; } = new();
    public List<JToken> UnreadableLostAndFound { get; } = new();

    public int UnreadableCount => UnreadableLedger.Count + UnreadableStates.Count + UnreadableLostAndFound.Count;
}

/// <summary>
/// Reads and writes <c>Multiplayer.Items</c>, the one key every item is saved in (G4; D1). Enums are
/// written by name, so appending values never shifts a saved one.
/// </summary>
public static class ItemsSaveCodec
{
    public const string Key = "Items";
    public const int Format = 1;

    /// <summary>Where items were saved before this key (D1): ignored, logged once, never written again.</summary>
    public static readonly string[] OldKeys = { "WorldItemsV1", "OwnedItemsV1", "LostAndFound", "ColdContainers" };

    public enum ReadResult : byte
    {
        /// <summary>No key: a new career, or one saved before it (D1).</summary>
        Absent,
        Read,
        /// <summary>Written by a newer build: left as it is.</summary>
        Newer
    }

    public static ReadResult Read(JToken saved, out ItemsSave items)
    {
        items = new ItemsSave();
        if (saved is not JObject root)
            return ReadResult.Absent;
        int format = (int?)root["format"] ?? 0;
        if (format > Format)
            return ReadResult.Newer;

        foreach (JToken value in root["ledger"] as JArray ?? new JArray())
        {
            LedgerEntry entry = value is JObject row ? ReadEntry(row) : null;
            if (entry == null || items.LedgerRows.ContainsKey(entry.Id))
            {
                items.UnreadableLedger.Add(value.DeepClone());
                continue;
            }
            items.Ledger.Add(entry);
            items.LedgerRows[entry.Id] = (JObject)value.DeepClone();
        }
        if (root["states"] is JObject states)
            foreach (JProperty state in states.Properties())
            {
                if (Guid.TryParse(state.Name, out Guid id) && id != Guid.Empty && state.Value is JObject row)
                    items.States[id] = (JObject)row.DeepClone();
                else
                    items.UnreadableStates[state.Name] = state.Value.DeepClone();
            }
        items.Unowned.AddRange(Objects(root["unowned"]));
        items.Authored.AddRange(Objects(root["authored"]));
        items.Containers.AddRange(Objects(root["containers"]));
        foreach (JToken value in root["lostAndFound"] as JArray ?? new JArray())
        {
            if (value is JObject row && Guid.TryParse((string)row["id"], out Guid id) && id != Guid.Empty &&
                !items.LostAndFound.ContainsKey(id))
                items.LostAndFound[id] = (JObject)row.DeepClone();
            else
                items.UnreadableLostAndFound.Add(value.DeepClone());
        }
        if (root["players"] is JObject players)
            foreach (JProperty player in players.Properties())
                if (Guid.TryParse(player.Name, out Guid id) && (bool?)player.Value?["starterKit"] == true)
                    items.StarterKits.Add(id);
        return ReadResult.Read;
    }

    public static JObject Write(ItemsSave items)
    {
        JArray ledger = new();
        foreach (LedgerEntry entry in items.Ledger)
            ledger.Add(items.LedgerRows.TryGetValue(entry.Id, out JObject kept) ? kept : WriteEntry(entry));
        foreach (JToken unreadable in items.UnreadableLedger)
            ledger.Add(unreadable);
        JObject states = new();
        foreach (KeyValuePair<Guid, JObject> state in items.States.OrderBy(pair => pair.Key))
            states[state.Key.ToString("D")] = state.Value;
        foreach (KeyValuePair<string, JToken> unreadable in items.UnreadableStates)
            if (states[unreadable.Key] == null)
                states[unreadable.Key] = unreadable.Value;
        JArray lost = new(items.LostAndFound.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        foreach (JToken unreadable in items.UnreadableLostAndFound)
            lost.Add(unreadable);
        JObject players = new();
        foreach (Guid player in items.StarterKits.Distinct().OrderBy(id => id))
            players[player.ToString("D")] = new JObject { ["starterKit"] = true };
        return new JObject
        {
            ["format"] = Format,
            ["ledger"] = ledger,
            ["states"] = states,
            ["unowned"] = new JArray(items.Unowned),
            ["authored"] = new JArray(items.Authored),
            ["containers"] = new JArray(items.Containers),
            ["lostAndFound"] = lost,
            ["players"] = players
        };
    }

    public static JObject WriteEntry(LedgerEntry entry)
    {
        Home home = entry.Home;
        JObject where = new() { ["kind"] = home.Kind.ToString() };
        if (home.IsWithPlayer)
            where["holder"] = home.Holder.ToString("D");
        if (home.Slot >= 0)
            where["slot"] = home.Slot;
        if (home.Hand != 0)
            where["hand"] = home.Hand;
        if (home.Anchor.Length > 0)
            where["anchor"] = home.Anchor;
        if (home.Kind == HomeKind.Attached)
            where["front"] = home.Front;

        JObject born = new()
        {
            ["reason"] = entry.Born.Reason.ToString(),
            ["utc"] = entry.Born.UtcTicks
        };
        if (entry.Born.By != Guid.Empty)
            born["by"] = entry.Born.By.ToString("D");
        if (entry.Born.From != Guid.Empty)
            born["from"] = entry.Born.From.ToString("D");
        if (entry.Born.Detail.Length > 0)
            born["detail"] = entry.Born.Detail;

        JObject item = new()
        {
            ["id"] = entry.Id.ToString("D"),
            ["owner"] = entry.Owner.PlayerId.ToString("D"),
            ["prefab"] = entry.Prefab,
            ["where"] = where,
            ["born"] = born
        };
        if (entry.ClaimSlot >= 0)
            item["claimSlot"] = entry.ClaimSlot;
        else if (entry.Essential)
            item["essential"] = true;
        if (entry.ShopStocked)
            item["shopStocked"] = true;
        if (entry.Ended != null)
        {
            JObject ended = new()
            {
                ["reason"] = entry.Ended.Reason.ToString(),
                ["utc"] = entry.Ended.UtcTicks
            };
            if (entry.Ended.ReplacedBy != Guid.Empty)
                ended["replacedBy"] = entry.Ended.ReplacedBy.ToString("D");
            if (entry.Ended.Detail.Length > 0)
                ended["detail"] = entry.Ended.Detail;
            item["ended"] = ended;
        }
        return item;
    }

    /// <summary>A saved entry, or null when it can't be read (no id, no owner, a home or birth it doesn't know).</summary>
    public static LedgerEntry ReadEntry(JObject item)
    {
        if (!Guid.TryParse((string)item["id"], out Guid id) || id == Guid.Empty ||
            !Guid.TryParse((string)item["owner"], out Guid owner) || owner == Guid.Empty ||
            item["where"] is not JObject where || item["born"] is not JObject born ||
            !Enum.TryParse((string)where["kind"], out HomeKind place) ||
            !Enum.TryParse((string)born["reason"], out BirthReason birth))
            return null;
        Guid.TryParse((string)where["holder"], out Guid holder);
        Home at = Home.From(place, holder, (int?)where["slot"] ?? -1, (byte?)where["hand"] ?? 0,
            (string)where["anchor"], (bool?)where["front"] ?? false);
        if (at.Kind == HomeKind.Unloadable)
            return null;
        Guid.TryParse((string)born["by"], out Guid by);
        Guid.TryParse((string)born["from"], out Guid from);
        End ended = null;
        if (item["ended"] is JObject end)
        {
            if (!Enum.TryParse((string)end["reason"], out ItemRemovalReason reason))
                reason = ItemRemovalReason.Unknown;
            Guid.TryParse((string)end["replacedBy"], out Guid replacedBy);
            ended = new End(reason, (long?)end["utc"] ?? 0, replacedBy, (string)end["detail"]);
        }
        return new LedgerEntry(id, OwnerRef.Player(owner), (string)item["prefab"], at,
            new Birth(birth, (long?)born["utc"] ?? 0, by, from, (string)born["detail"]),
            (int?)item["claimSlot"] ?? -1, (bool?)item["shopStocked"] ?? false, ended,
            (bool?)item["essential"] ?? false);
    }

    /// <summary>A container's row: its id (its shell's item id, OI-CON-09) and its size. Its owner is the ledger's, so it has none.</summary>
    public static JObject WriteContainer(ContainerRecord container)
    {
        JObject row = new()
        {
            ["id"] = container.PersistentContainerId.ToString("D"),
            ["prefab"] = container.PrefabName ?? string.Empty,
            ["capacity"] = container.Capacity
        };
        return row;
    }

    /// <summary>A saved container, or null when it can't be read (no id, no size). An owner saved by an earlier build is ignored.</summary>
    public static ContainerRecord ReadContainer(JObject row)
    {
        if (!Guid.TryParse((string)row["id"], out Guid id) || id == Guid.Empty ||
            ((int?)row["capacity"] ?? 0) <= 0)
            return null;
        return new ContainerRecord
        {
            PersistentContainerId = id,
            PrefabName = (string)row["prefab"] ?? string.Empty,
            Capacity = (int)row["capacity"]
        };
    }

    /// <summary>A Lost and Found entry's row: a reason and a time (its owner and claim are the ledger's).</summary>
    public static JObject WriteLost(LostItemRecord entry)
    {
        JObject row = new()
        {
            ["id"] = entry.PersistentItemId.ToString("D"),
            ["reason"] = entry.Reason.ToString(),
            ["utc"] = entry.LostUtcTicks
        };
        if (!string.IsNullOrEmpty(entry.DisplayName))
            row["display"] = entry.DisplayName;
        if (entry.InventoryClaimFlags != 0)
            row["claimFlags"] = entry.InventoryClaimFlags;
        return row;
    }

    private static IEnumerable<JToken> Objects(JToken section) =>
        (section as JArray ?? new JArray()).Select(value => value.DeepClone());
}
