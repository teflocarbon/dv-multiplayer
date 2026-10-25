using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Items.WorldItems;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Multiplayer.Shared.Items.WorldItems;

/// <summary>
/// The shelves (<c>ItemStaticParent</c>) of the loaded scenes, by key (docs/design/items/
/// world-items.md, "Shelves"). Their key has no position, so a moved shelf still resolves, and of
/// shelves with one key the one that puts the item's shelf-local position nearest where the item is
/// meant (StaticParentKeys). A scene loaded or unloaded since the last scan rescans them before any
/// lookup, so a tile with a same-keyed shelf is a candidate at once. Otherwise a miss rescans at most
/// every <see cref="RebuildSeconds"/>: before R7 every miss rescanned them, and a key no game here has
/// (another mod's shelf, an old save's) did it on every sample (OI-SYNC-09, F47).
/// </summary>
internal static class WorldItemStaticParentRegistry
{
    private const float RebuildSeconds = 2f;
    private static readonly Dictionary<string, List<Transform>> parents = new();
    private static float rebuiltAt = float.NegativeInfinity;
    private static bool scenesChanged = true;

    static WorldItemStaticParentRegistry()
    {
        SceneManager.sceneLoaded += (_, _) => scenesChanged = true;
        SceneManager.sceneUnloaded += _ => scenesChanged = true;
    }

    /// <param name="absolute">Where the item is, as an absolute position.</param>
    /// <param name="parentLocal">The item's position in the shelf's frame. Of shelves with one key, the
    /// one that puts it nearest <paramref name="absolute"/>: two same-named shelves in one building are
    /// told apart by where the item sits, not by which pivot is nearer.</param>
    public static bool TryResolve(string key, Vector3 absolute, Vector3 parentLocal, out Transform parent)
    {
        if (scenesChanged)
            Rebuild();
        if (Pick(key, absolute, parentLocal, out parent))
            return true;
        if (Time.realtimeSinceStartup - rebuiltAt < RebuildSeconds)
            return false;
        Rebuild();
        return Pick(key, absolute, parentLocal, out parent);
    }

    public static void Rebuild()
    {
        parents.Clear();
        scenesChanged = false;
        rebuiltAt = Time.realtimeSinceStartup;
        foreach (ItemStaticParent candidate in Resources.FindObjectsOfTypeAll<ItemStaticParent>())
        {
            if (candidate == null || !candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded) continue;
            string key = WorldItemStableIdentity.CaptureStaticParent(candidate.transform);
            if (string.IsNullOrEmpty(key)) continue;
            if (!parents.TryGetValue(key, out List<Transform> same))
                parents[key] = same = new List<Transform>();
            same.Add(candidate.transform);
        }
    }

    public static void Clear()
    {
        parents.Clear();
        scenesChanged = true;
    }

    private static bool Pick(string key, Vector3 absolute, Vector3 parentLocal, out Transform parent)
    {
        parent = null;
        if (!parents.TryGetValue(key ?? string.Empty, out List<Transform> same))
            return false;
        same.RemoveAll(candidate => candidate == null);
        if (same.Count == 0)
        {
            parents.Remove(key);
            return false;
        }
        Vector3 near = absolute + WorldMover.currentMove;
        parent = same[StaticParentKeys.Nearest(same.Select(candidate =>
            (candidate.TransformPoint(parentLocal) - near).sqrMagnitude).ToArray())];
        return true;
    }
}
