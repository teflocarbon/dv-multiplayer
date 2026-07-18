using DV.CabControls;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

/// <summary>Inactive-inclusive catalogue of scene-authored item representations.</summary>
internal sealed class AuthoredWorldItemCatalogue
{
    private readonly Dictionary<string, NetworkedItem> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> collisions = new(StringComparer.Ordinal);

    public int Count => entries.Count;
    public int CollisionCount => collisions.Count;
    public IEnumerable<NetworkedItem> Items => entries.Values;

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
            if (entries.TryGetValue(networked.AuthoredItemKey, out NetworkedItem existing) && existing != networked)
            {
                collisions.Add(networked.AuthoredItemKey);
                continue;
            }
            entries[networked.AuthoredItemKey] = networked;
        }

        DebugRuntime.Publish("item-world", "world-item.catalogue-built",
            NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
            collisions.Count == 0 ? DebugSeverity.Info : DebugSeverity.Error,
            data: new Dictionary<string, object>
            {
                ["count"] = Count,
                ["collisionCount"] = CollisionCount,
                ["digest"] = Digest()
            });
    }

    public string Digest()
    {
        using SHA256 sha = SHA256.Create();
        string material = string.Join("\n", entries.Keys.OrderBy(value => value, StringComparer.Ordinal));
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
        return BitConverter.ToString(hash, 0, 16).Replace("-", string.Empty).ToLowerInvariant();
    }

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
