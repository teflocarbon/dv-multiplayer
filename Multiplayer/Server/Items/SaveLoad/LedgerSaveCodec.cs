using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// The ledger's save format, <c>Multiplayer.OwnedItemsV1</c> (docs/design/items/ledger.md, "Saving
/// the ledger"). Enums are written by name, so appending values never shifts a saved one. Format 2
/// writes exact homes; a format-1 ledger is read as none, and the load migrates from the stores.
/// </summary>
public static class LedgerSaveCodec
{
    public const int Format = 2;

    public static JObject Write(ItemLedger ledger)
    {
        JArray items = new();
        foreach (LedgerEntry entry in ledger.All())
            items.Add(Write(entry));
        JObject players = new();
        foreach (Guid player in ledger.StarterKitsGiven.OrderBy(id => id))
            players[player.ToString("D")] = new JObject { ["starterKit"] = true };
        return new JObject { ["format"] = Format, ["items"] = items, ["players"] = players };
    }

    /// <summary>
    /// Reads a saved ledger. Entries that can't be read are skipped and counted in
    /// <paramref name="unreadable"/>; a format from a newer build reads nothing.
    /// </summary>
    public static List<LedgerEntry> Read(JToken saved, out List<Guid> starterKits, out int unreadable)
    {
        List<LedgerEntry> entries = new();
        starterKits = new List<Guid>();
        unreadable = 0;
        if (saved is not JObject root || ((int?)root["format"] ?? 0) != Format)
            return entries;
        foreach (JToken value in root["items"] as JArray ?? new JArray())
        {
            LedgerEntry entry = value is JObject item ? Read(item) : null;
            if (entry != null)
                entries.Add(entry);
            else
                unreadable++;
        }
        if (root["players"] is JObject players)
            foreach (KeyValuePair<string, JToken> player in players)
                if (Guid.TryParse(player.Key, out Guid id) && (bool?)player.Value?["starterKit"] == true)
                    starterKits.Add(id);
        return entries;
    }

    private static JObject Write(LedgerEntry entry)
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

    private static LedgerEntry Read(JObject item)
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
}
