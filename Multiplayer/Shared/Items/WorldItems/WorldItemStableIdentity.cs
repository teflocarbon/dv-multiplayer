using DV.CabControls;
using Multiplayer.Core.Items.WorldItems;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Multiplayer.Shared.Items.WorldItems;

/// <summary>
/// Deterministic identity for a scene-authored item. The key is captured before DV reparents
/// content. Objects first discovered after migration are dynamic and are never promoted by
/// transform or prefab-name heuristics.
/// </summary>
internal static class WorldItemStableIdentity
{
    public const int KeyHexLength = 32;

    public static string Capture(ItemBase item)
    {
        if (item == null)
            return string.Empty;

        string scene = item.gameObject.scene.name ?? string.Empty;
        string hierarchy = HierarchyPath(item.transform);
        string prefab = item.InventorySpecs?.ItemPrefabName ?? item.name ?? string.Empty;
        Vector3 absolute = item.transform.position - WorldMover.currentMove;
        Quaternion rotation = item.transform.rotation;
        string material = string.Join("|", new[]
        {
            NormalizeScene(scene), hierarchy, prefab,
            F(absolute.x), F(absolute.y), F(absolute.z),
            F(rotation.x), F(rotation.y), F(rotation.z), F(rotation.w)
        });

        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
        StringBuilder key = new(KeyHexLength);
        for (int i = 0; i < KeyHexLength / 2; i++)
            key.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        return key.ToString();
    }

    public static bool IsLikelyAuthoredAtAwake(ItemBase item)
    {
        if (item == null || !item.gameObject.scene.IsValid())
            return false;
        string scene = item.gameObject.scene.name ?? string.Empty;
        if (scene.StartsWith("game_content_", StringComparison.OrdinalIgnoreCase))
            return true;
        Transform root = item.transform.root;
        return root != null && string.Equals(root.name, "[origin shift content]", StringComparison.Ordinal);
    }

    public static bool IsValid(string key) =>
        !string.IsNullOrEmpty(key) && key.Length == KeyHexLength;

    /// <summary>
    /// A shelf's key, the same in every game: its path by names (StaticParentKeys, OI-SYNC-09). The
    /// key before R7 hashed sibling indices and the absolute position, and differed between games.
    /// </summary>
    public static string CaptureStaticParent(Transform transform)
    {
        if (transform == null) return string.Empty;
        List<string> names = new();
        for (Transform current = transform; current != null; current = current.parent)
            names.Insert(0, current.name);
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(StaticParentKeys.Material(names)));
        StringBuilder key = new(KeyHexLength);
        for (int i = 0; i < KeyHexLength / 2; i++) key.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        return key.ToString();
    }

    private static string NormalizeScene(string scene) =>
        scene.StartsWith("game_content_", StringComparison.OrdinalIgnoreCase)
            ? "game_content"
            : scene;

    internal static string HierarchyPath(Transform transform)
    {
        StringBuilder result = new();
        for (Transform current = transform; current != null; current = current.parent)
        {
            if (result.Length > 0)
                result.Insert(0, '/');
            result.Insert(0, current.name + "#" + current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    // Millimetre precision survives origin shifting while avoiding insignificant float noise.
    private static string F(float value) =>
        Math.Round(value, 3).ToString("0.000", CultureInfo.InvariantCulture);
}
