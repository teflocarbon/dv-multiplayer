using System;
using System.Collections.Generic;
using System.Linq;
using DV.Items;
using Multiplayer.Shared.Items.LiveState;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// A loaded magazine as a stand-in (docs/design/items/containers.md, "Ammo in a magazine";
/// OI-CON-16): its ammo is rows nested in its row. They ride in the tool's own state, under
/// <see cref="Key"/>, so everything that carries a stand-in's state (a stash, an inventory row, the
/// stand-in itself) carries its ammo too. Each names the ammo's NetId, which the server keeps
/// reserved while the tool is data, so the take-out makes the ammo with the tool, under those
/// NetIds, on the holder's game and the server alike.
/// </summary>
internal static class StandInAmmo
{
    /// <summary>The key the nested ammo rows have in a stand-in's state. Never in an object's own state.</summary>
    public const string Key = "mpMagazineAmmo";

    public sealed class Entry
    {
        public int Slot = -1;
        public ushort NetId;
        public string PrefabName = string.Empty;
        public uint Revision;
        public JObject State;
    }

    /// <summary>The tool's magazine, if it has exactly one (as every vanilla tool does).</summary>
    public static ItemMagazine MagazineOf(GameObject tool)
    {
        ItemMagazine[] magazines = tool != null ? tool.GetComponentsInChildren<ItemMagazine>(true) : Array.Empty<ItemMagazine>();
        return magazines.Length == 1 ? magazines[0] : null;
    }

    /// <summary>What the tool's magazine holds, by slot.</summary>
    public static IEnumerable<(int Slot, GameObject Ammo)> Loaded(GameObject tool)
    {
        ItemMagazine magazine = MagazineOf(tool);
        if (magazine == null)
            yield break;
        for (int slot = 0; slot < magazine.Capacity; slot++)
            if (magazine[slot] != null)
                yield return (slot, magazine[slot]);
    }

    /// <summary>
    /// Whether a loaded tool can be a stand-in: one magazine, and each item in it a networked item
    /// whose prefab can be a row. Not a boombox playing its cassette, which a stand-in can't play
    /// (<see cref="StashedItems.PlaysCassette"/>).
    /// </summary>
    public static bool CanNest(GameObject tool) =>
        MagazineOf(tool) != null && !StashedItems.PlaysCassette(tool) && Loaded(tool).All(loaded =>
            loaded.Ammo.TryGetComponent(out NetworkedItem ammo) && ammo.NetId != 0 && !ammo.ColdStorageRetirement &&
            loaded.Ammo.GetComponent<InventoryItemSpec>() is { } spec && InventoryStandIn.CanStandIn(spec.ItemPrefabName));

    /// <summary>The tool's state as a stand-in keeps it: its own, with its loaded ammo nested.</summary>
    public static string Capture(GameObject tool)
    {
        string own = ItemStateText.Capture(tool);
        List<Entry> entries = new();
        foreach ((int slot, GameObject loaded) in Loaded(tool))
        {
            if (!loaded.TryGetComponent(out NetworkedItem ammo) || ammo.NetId == 0)
                continue;
            entries.Add(new Entry
            {
                Slot = slot,
                NetId = ammo.NetId,
                PrefabName = loaded.GetComponent<InventoryItemSpec>()?.ItemPrefabName ?? string.Empty,
                Revision = ammo.AuthorityRevision,
                State = Parse(ItemStateText.Capture(loaded))
            });
        }
        return With(own, entries);
    }

    /// <summary>A state with these ammo rows nested in it (none: the state without any).</summary>
    public static string With(string state, IEnumerable<Entry> entries)
    {
        List<Entry> list = entries?.ToList() ?? new List<Entry>();
        if (list.Count == 0)
            return Without(state);
        JObject parsed = Parse(state) ?? new JObject();
        parsed[Key] = new JArray(list.Select(entry => new JObject
        {
            ["slot"] = entry.Slot,
            ["netId"] = entry.NetId,
            ["prefab"] = entry.PrefabName ?? string.Empty,
            ["revision"] = entry.Revision,
            ["state"] = entry.State ?? new JObject()
        }));
        return parsed.ToString(Formatting.None);
    }

    /// <summary>The ammo rows nested in a stand-in's state.</summary>
    public static List<Entry> Of(string state)
    {
        List<Entry> entries = new();
        if (string.IsNullOrEmpty(state) || !state.Contains(Key) || Parse(state)?[Key] is not JArray rows)
            return entries;
        foreach (JToken token in rows)
        {
            if (token is not JObject row)
                continue;
            entries.Add(new Entry
            {
                Slot = (int?)row["slot"] ?? -1,
                NetId = (ushort?)row["netId"] ?? 0,
                PrefabName = (string)row["prefab"] ?? string.Empty,
                Revision = (uint?)row["revision"] ?? 0,
                State = row["state"] as JObject
            });
        }
        return entries.Where(entry => entry.NetId != 0 && entry.Slot >= 0).ToList();
    }

    /// <summary>A stand-in's state without its ammo: the tool's own, as its object loads it.</summary>
    public static string Without(string state)
    {
        if (string.IsNullOrEmpty(state) || !state.Contains(Key) || Parse(state) is not { } parsed)
            return state ?? string.Empty;
        parsed.Remove(Key);
        return parsed.ToString(Formatting.None);
    }

    /// <summary>
    /// The tool's ammo objects go with it as it becomes a stand-in or data: each is let out of the
    /// magazine as a retirement (so no game reports it moved) and handed to <paramref name="retire"/>,
    /// before the tool's object goes (vanilla empties a destroyed magazine into the world).
    /// </summary>
    public static void RetireLoaded(GameObject tool, Action<NetworkedItem> retire)
    {
        ItemMagazine magazine = MagazineOf(tool);
        foreach ((int slot, GameObject loaded) in Loaded(tool).ToArray())
        {
            if (!loaded.TryGetComponent(out NetworkedItem ammo))
                continue;
            ammo.BeginColdStorageRetirement();
            magazine.RemoveItem(slot, false, false);
            retire(ammo);
        }
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
