#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests.TrainFixtures;

internal sealed class DebugTrainCabAnchor
{
    public Transform Parent { get; set; }
    public Transform TeleportTarget { get; set; }
    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }
    public BoxCollider WalkableSurface { get; set; }
    public string Source { get; set; } = string.Empty;
    public string LiveryId { get; set; } = string.Empty;
}

/// <summary>
/// Resolves a player standing pose from authored train-interior geometry. TrainCar.interior is a
/// separately managed physics root in DV and is not guaranteed to be a child of the TrainCar.
/// Consequently every lookup deliberately starts at the interior transform.
/// </summary>
internal static class DebugTrainCabAnchorResolver
{
    private sealed class LiveryAnchor
    {
        public string SurfaceName { get; set; } = string.Empty;
        public Vector3 FallbackLocalPosition { get; set; }
        public float LocalYaw { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, LiveryAnchor> Liveries =
        new Dictionary<string, LiveryAnchor>(StringComparer.OrdinalIgnoreCase)
        {
            // Verified at runtime on host and client against LocoDE2's authored area_cab:
            // footprint 2.86 x 1.8857 m, top surface y ~= 1.556, centre z ~= -0.414.
            ["LocoDE2"] = new()
            {
                SurfaceName = "area_cab",
                FallbackLocalPosition = new Vector3(0f, 1.581f, -0.414f),
                LocalYaw = 0f
            }
        };

    private const float FloorClearance = 0.025f;
    private const float MinimumStandingWidth = 0.7f;

    public static bool TryResolve(TrainCar car, out DebugTrainCabAnchor anchor,
        out string error)
    {
        anchor = null;
        error = string.Empty;
        if (car == null)
        {
            error = "car-unavailable";
            return false;
        }

        Transform interior = car.interior;
        if (interior == null)
        {
            error = "interior-unavailable";
            return false;
        }

        string liveryId = car.carLivery?.id ?? string.Empty;
        CharacterReparentTarget reparentTarget = interior.GetComponentInChildren<
            CharacterReparentTarget>(true);
        Transform parent = reparentTarget?.target ?? interior;
        Transform teleportTarget = reparentTarget?.transform ?? interior;
        Liveries.TryGetValue(liveryId, out LiveryAnchor configured);
        BoxCollider[] surfaces = interior.GetComponentsInChildren<BoxCollider>(true);
        BoxCollider surface = configured == null ? null : surfaces.FirstOrDefault(candidate =>
            string.Equals(candidate.name, configured.SurfaceName,
                StringComparison.OrdinalIgnoreCase) && IsStandingSurface(candidate));
        surface ??= surfaces.Where(IsStandingSurface)
            .OrderByDescending(CandidateScore).FirstOrDefault();

        if (surface != null)
        {
            Vector3 localPosition = SurfaceStandingPoint(interior, surface);
            anchor = new DebugTrainCabAnchor
            {
                Parent = parent,
                TeleportTarget = teleportTarget,
                LocalPosition = localPosition,
                LocalRotation = Quaternion.Euler(0f, configured?.LocalYaw ?? 0f, 0f),
                WalkableSurface = surface,
                Source = configured != null && string.Equals(surface.name,
                    configured.SurfaceName, StringComparison.OrdinalIgnoreCase)
                    ? "livery-authored-surface" : "walkable-surface-fallback",
                LiveryId = liveryId
            };
            return true;
        }

        if (configured != null)
        {
            anchor = new DebugTrainCabAnchor
            {
                Parent = parent,
                TeleportTarget = teleportTarget,
                LocalPosition = configured.FallbackLocalPosition,
                LocalRotation = Quaternion.Euler(0f, configured.LocalYaw, 0f),
                Source = "livery-local-fallback",
                LiveryId = liveryId
            };
            return true;
        }

        error = "cab-walkable-surface-unavailable:" + liveryId;
        return false;
    }

    public static bool ContainsPlayer(DebugTrainCabAnchor anchor, Vector3 worldPosition)
    {
        if (anchor?.WalkableSurface == null) return true;
        BoxCollider surface = anchor.WalkableSurface;
        Vector3 point = surface.transform.InverseTransformPoint(worldPosition);
        Vector3 half = surface.size * 0.5f;
        const float horizontalTolerance = 0.15f;
        return Mathf.Abs(point.x - surface.center.x) <= half.x + horizontalTolerance &&
            Mathf.Abs(point.z - surface.center.z) <= half.z + horizontalTolerance &&
            point.y >= surface.center.y + half.y - 0.3f &&
            point.y <= surface.center.y + half.y + 0.75f;
    }

    private static bool IsStandingSurface(BoxCollider surface)
    {
        if (surface == null || surface.isTrigger) return false;
        Vector3 size = surface.size;
        if (size.x < MinimumStandingWidth || size.z < MinimumStandingWidth) return false;
        string name = surface.name ?? string.Empty;
        bool cabLike = name.IndexOf("cab", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("area", StringComparison.OrdinalIgnoreCase) >= 0;
        return cabLike || surface.gameObject.layer == LayerMask.NameToLayer("Train_Walkable");
    }

    private static float CandidateScore(BoxCollider surface)
    {
        string name = surface.name ?? string.Empty;
        float score = surface.size.x * surface.size.z;
        if (name.IndexOf("cab", StringComparison.OrdinalIgnoreCase) >= 0) score += 1000f;
        if (name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0) score += 250f;
        if (surface.gameObject.layer == LayerMask.NameToLayer("Train_Walkable")) score += 100f;
        if (surface.gameObject.activeInHierarchy) score += 25f;
        return score;
    }

    private static Vector3 SurfaceStandingPoint(Transform interior, BoxCollider surface)
    {
        Vector3 localTop = surface.center + Vector3.up * surface.size.y * 0.5f;
        Vector3 worldTop = surface.transform.TransformPoint(localTop);
        Vector3 worldStanding = worldTop + surface.transform.up * FloorClearance;
        return interior.InverseTransformPoint(worldStanding);
    }
}
#endif
