using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Dvmp.Agent;

/// <summary>
/// Small helpers for probes. Positions exchanged between host and client must be absolute
/// (origin shift removed): each process has its own floating origin.
/// </summary>
public static class Dev
{
    /// <summary>World position with this process's origin shift removed, comparable across peers.</summary>
    public static Vector3 Abs(Vector3 worldPosition) => worldPosition - WorldMover.currentMove;

    /// <summary>Converts an absolute position back into this process's shifted world space.</summary>
    public static Vector3 World(Vector3 absolutePosition) => absolutePosition + WorldMover.currentMove;

    public static Vector3? PlayerAbs => PlayerManager.PlayerTransform != null
        ? Abs(PlayerManager.PlayerTransform.position)
        : null;

    /// <summary>Hierarchy path of a transform, e.g. "L-036/[interior]/lantern".</summary>
    public static string Path(Component c) => c == null ? null : Json.PathOf(c.transform);

    public static string Path(GameObject g) => g == null ? null : Json.PathOf(g.transform);

    /// <summary>All loaded instances of a component type, including inactive ones.</summary>
    public static List<T> All<T>() where T : Object => Resources.FindObjectsOfTypeAll<T>()
        .Where(o => o is not Component c || c.gameObject.scene.IsValid())
        .ToList();

    /// <summary>Instances within <paramref name="radius"/> metres of the local player.</summary>
    public static List<T> Near<T>(float radius) where T : Component
    {
        if (PlayerManager.PlayerTransform == null) return new List<T>();
        Vector3 player = PlayerManager.PlayerTransform.position;
        float r2 = radius * radius;
        return Object.FindObjectsOfType<T>().Where(c => (c.transform.position - player).sqrMagnitude <= r2).ToList();
    }

    /// <summary>A Unity object reduced to type/name/id/path, as the serializer would report it.</summary>
    public static object Stub(Object obj) => Json.Stub(obj);
}
