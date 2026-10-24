using Multiplayer.Server.Career;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Server.Items.SaveLoad;

/// <summary>
/// Saving and loading every item (docs/design/items/save-load.md): one key, <c>Multiplayer.Items</c>,
/// written in one pass (<c>.Save</c>) and read in one pass, once, before any request is served
/// (<c>.Load</c>); the objects are made from rows by <c>.Make</c>. Vanilla's item lists are a mirror
/// it writes and never reads (D2).
/// </summary>
internal static partial class ItemSaveLoad
{
    private static bool loaded;
    // Written by a newer build: kept as it was, and written back unchanged.
    private static JToken newerKey;
    private static readonly List<JObject> destroyedAuthoredRecords = new();

    /// <summary>
    /// Rows the load couldn't make or read, kept verbatim and written back unchanged at each save
    /// (D9, OI-PER-15): an unloadable owned item's ledger, state and Lost and Found rows by id, and
    /// the rest by section.
    /// </summary>
    private static readonly Dictionary<Guid, KeptItem> keptOwned = new();
    private static readonly KeptRows kept = new();

    private sealed class KeptItem
    {
        public JObject Ledger;
        public JObject State;
        public JObject LostAndFound;
    }

    private sealed class KeptRows
    {
        public readonly List<JToken> Ledger = new();
        public readonly Dictionary<string, JToken> States = new();
        public readonly List<JToken> Unowned = new();
        public readonly List<JToken> Authored = new();
        public readonly List<JToken> Containers = new();
        public readonly List<JToken> LostAndFound = new();

        public IEnumerable<JToken> All() => Ledger.Concat(States.Values).Concat(Unowned).Concat(Authored)
            .Concat(Containers).Concat(LostAndFound);

        public void Clear()
        {
            Ledger.Clear();
            States.Clear();
            Unowned.Clear();
            Authored.Clear();
            Containers.Clear();
            LostAndFound.Clear();
        }
    }

    /// <summary>The load has run: every saved item is made, kept as data, or kept as its row.</summary>
    public static bool Loaded => loaded;

    public static void Clear()
    {
        loaded = false;
        newerKey = null;
        destroyedAuthoredRecords.Clear();
        keptOwned.Clear();
        kept.Clear();
        ClearMake();
        WorldItemStaticParentRegistry.Clear();
    }

    public static void HostTick()
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            return;
        TryLoad();
        ProcessPendingStateRestores();
    }

    /// <summary>Whether any row the load kept as it was names this identity.</summary>
    public static bool KeptNames(Guid identity) =>
        identity != Guid.Empty && KeptTokens().Any(token => InstallGuidRekey.TokenNames(token, identity));

    /// <summary>A player's identity changed (InstallGuidRekey): the kept rows follow. Returns how many changed.</summary>
    public static int RekeyKept(Guid from, Guid to) =>
        KeptTokens().Sum(token => InstallGuidRekey.RekeyToken(token, from, to));

    private static IEnumerable<JToken> KeptTokens() => keptOwned.Values
        .SelectMany(item => new JToken[] { item.Ledger, item.State, item.LostAndFound })
        .Concat(kept.All()).Where(token => token != null);

    public static void RecordDestroyed(NetworkedItem item)
    {
        if (item == null || !item.IsSceneAuthored ||
            !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return;

        ReplaceAuthoredTombstone(CreateTombstone(record).ToJson(owned: false));
    }

    private static SavedItemRow CreateTombstone(LiveItems.LiveItem record) =>
        new()
        {
            AuthoredItemKey = record.AuthoredItemKey ?? string.Empty,
            PrefabName = record.PrefabName ?? string.Empty,
            Revision = record.Revision == uint.MaxValue ? record.Revision : record.Revision + 1,
            Placement = ItemPlacementKind.Destroyed
        };

    private static void ReplaceAuthoredTombstone(JObject tombstone)
    {
        string authoredKey = (string)tombstone?["authoredKey"] ?? string.Empty;
        destroyedAuthoredRecords.RemoveAll(existing =>
            string.Equals((string)existing["authoredKey"], authoredKey, StringComparison.Ordinal));
        if (tombstone != null)
            destroyedAuthoredRecords.Add(tombstone);
    }

    /// <summary>
    /// Job overviews and booklets belong to DV's job system, which saves jobs and recreates their
    /// paperwork on load. Saving them as items as well made a dead copy of every board overview on
    /// each load, piling stacked duplicates onto the job boards.
    /// </summary>
    internal static bool IsJobSystemDocument(string prefabName) =>
        prefabName is "JobOverview" or "JobBooklet";

    /// <summary>An item in a player's inventory or hand, connected or not, the host's own player included.</summary>
    internal static bool IsPlayerPossession(LiveItems.LiveItem record) =>
        record?.Placement is ItemPlacementKind.PlayerInventory or ItemPlacementKind.PlayerHand &&
        record.Holder != Guid.Empty;
}
