using DV.CabControls;
using Multiplayer.Core.Items.WorldItems;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Multiplayer.Shared.Items.WorldItems;

/// <summary>Inactive-inclusive catalogue of scene-authored item representations.</summary>
internal sealed class AuthoredWorldItemCatalogue
{
    private readonly Dictionary<string, NetworkedItem> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> collisions = new(StringComparer.Ordinal);

    public int Count => entries.Count;
    public int CollisionCount => collisions.Count;
    public IEnumerable<NetworkedItem> Items => entries.Values;

    // The scene's catalogue as built, for the join check. The live entries lose a key while a
    // taken office prop waits to be replenished, so comparing them rejected a client that
    // joined in that window, and the host then never sent it a scene item.
    public int BaselineCount { get; private set; }
    public int BaselineCollisionCount => BaselineCollisionKeys.Length;
    /// <summary>The keys two of the scene's objects make: neither is bound by it (OI-SYNC-08).</summary>
    public string[] BaselineCollisionKeys { get; private set; } = Array.Empty<string>();
    public string BaselineDigest { get; private set; } = string.Empty;

    public bool TryGet(string key, out NetworkedItem item) => entries.TryGetValue(key ?? string.Empty, out item);

    public void Remove(string key, NetworkedItem expected)
    {
        if (!WorldItemStableIdentity.IsValid(key) ||
            !entries.TryGetValue(key, out NetworkedItem existing) || existing != expected)
            return;
        entries.Remove(key);
        collisions.Remove(key);
    }

    public void Replace(string key, NetworkedItem item)
    {
        if (!WorldItemStableIdentity.IsValid(key) || item == null) return;
        entries[key] = item;
        collisions.Remove(key);
    }

    public void Rebuild()
    {
        entries.Clear();
        collisions.Clear();

        // FindObjectsOfType excludes inactive authored office contents. Resources is intentional;
        // scene validity filters out prefab assets and editor-only objects.
        foreach (ItemBase item in Resources.FindObjectsOfTypeAll<ItemBase>())
        {
            if (!IsCatalogueCandidate(item))
                continue;
            NetworkedItem networked = item.GetComponent<NetworkedItem>() ?? item.gameObject.AddComponent<NetworkedItem>();
            if (!networked.IsSceneAuthored)
                continue;
            if (collisions.Contains(networked.AuthoredItemKey))
                continue;
            if (entries.TryGetValue(networked.AuthoredItemKey, out NetworkedItem existing) && existing != networked)
            {
                // Which of the two a game found first is its own: neither is bound.
                collisions.Add(networked.AuthoredItemKey);
                entries.Remove(networked.AuthoredItemKey);
                continue;
            }
            entries[networked.AuthoredItemKey] = networked;
        }
        // Every key the scenes have, a collided one too: two games must name the same keys.
        BaselineCount = Count + CollisionCount;
        BaselineCollisionKeys = collisions.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        BaselineDigest = AuthoredCatalogueKeys.Digest(entries.Keys.Concat(collisions));

        DebugRuntime.Publish("item-world", "world-item.catalogue-built",
            NetworkRole.DebugSide,
            collisions.Count == 0 ? DebugSeverity.Info : DebugSeverity.Error,
            data: new Dictionary<string, object>
            {
                ["count"] = Count,
                ["collisionCount"] = CollisionCount,
                ["collisionKeys"] = string.Join(",", BaselineCollisionKeys),
                ["digest"] = BaselineDigest
            });
    }

    public string Digest() => AuthoredCatalogueKeys.Digest(entries.Keys);

    private static bool IsCatalogueCandidate(ItemBase item)
    {
        if (item == null || !item.gameObject.scene.IsValid() || !item.gameObject.scene.isLoaded)
            return false;
        if (!item.gameObject.scene.name.StartsWith("game", StringComparison.OrdinalIgnoreCase))
            return false;
        if (item.name.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        try { return item.InventorySpecs != null; }
        catch { return false; }
    }
}
