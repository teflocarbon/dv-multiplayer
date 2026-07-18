using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.World.WorldItems;

internal static class WorldItemStaticParentRegistry
{
    private static readonly Dictionary<string, Transform> parents = new();

    public static bool TryResolve(string key, out Transform parent)
    {
        if (parents.TryGetValue(key ?? string.Empty, out parent) && parent != null) return true;
        Rebuild();
        return parents.TryGetValue(key ?? string.Empty, out parent) && parent != null;
    }

    public static void Rebuild()
    {
        parents.Clear();
        foreach (ItemStaticParent candidate in Resources.FindObjectsOfTypeAll<ItemStaticParent>())
        {
            if (candidate == null || !candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded) continue;
            string key = WorldItemStableIdentity.CaptureStaticParent(candidate.transform);
            if (!string.IsNullOrEmpty(key) && !parents.ContainsKey(key)) parents[key] = candidate.transform;
        }
    }

    public static void Clear() => parents.Clear();
}
