using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Core.Items;

public readonly struct TrainItemWakeBounds
{
    public readonly float CenterX;
    public readonly float CenterY;
    public readonly float CenterZ;
    public readonly float ExtentX;
    public readonly float ExtentY;
    public readonly float ExtentZ;

    public TrainItemWakeBounds(float centerX, float centerY, float centerZ,
        float extentX, float extentY, float extentZ)
    {
        CenterX = centerX;
        CenterY = centerY;
        CenterZ = centerZ;
        ExtentX = Math.Max(0.001f, extentX);
        ExtentY = Math.Max(0.001f, extentY);
        ExtentZ = Math.Max(0.001f, extentZ);
    }

    public float MinX => CenterX - ExtentX;
    public float MaxX => CenterX + ExtentX;
    public float MinY => CenterY - ExtentY;
    public float MaxY => CenterY + ExtentY;
    public float MinZ => CenterZ - ExtentZ;
    public float MaxZ => CenterZ + ExtentZ;
}

public readonly struct TrainItemWakeNode
{
    public readonly ushort ItemNetId;
    public readonly ushort TrainCarNetId;
    public readonly bool Settled;
    public readonly TrainItemWakeBounds Bounds;

    public TrainItemWakeNode(ushort itemNetId, ushort trainCarNetId, bool settled,
        TrainItemWakeBounds bounds)
    {
        ItemNetId = itemNetId;
        TrainCarNetId = trainCarNetId;
        Settled = settled;
        Bounds = bounds;
    }
}

/// <summary>
/// Pure, bounded support propagation for settled train items. It intentionally models only
/// upward support: side-by-side clutter is not woken merely because one object moved.
/// </summary>
public static class TrainItemWakePlanner
{
    public const int DefaultMaximumItems = 32;
    public const int DefaultMaximumDepth = 8;
    public const float DefaultVerticalTolerance = 0.12f;
    public const float DefaultMinimumHorizontalOverlap = 0.01f;

    public static ushort[] FromRemovedSupport(IEnumerable<TrainItemWakeNode> candidates,
        ushort trainCarNetId, TrainItemWakeBounds removedBounds,
        int maximumItems = DefaultMaximumItems, int maximumDepth = DefaultMaximumDepth,
        float verticalTolerance = DefaultVerticalTolerance,
        float minimumHorizontalOverlap = DefaultMinimumHorizontalOverlap)
    {
        TrainItemWakeNode[] nodes = Eligible(candidates, trainCarNetId);
        List<ushort> result = new(Math.Max(0, Math.Min(maximumItems, nodes.Length)));
        Queue<(TrainItemWakeBounds Bounds, int Depth)> frontier = new();
        frontier.Enqueue((removedBounds, 0));
        Propagate(nodes, frontier, result, maximumItems, maximumDepth,
            verticalTolerance, minimumHorizontalOverlap);
        return result.ToArray();
    }

    public static ushort[] FromSeeds(IEnumerable<TrainItemWakeNode> candidates,
        ushort trainCarNetId, IEnumerable<ushort> seedItemNetIds,
        int maximumItems = DefaultMaximumItems, int maximumDepth = DefaultMaximumDepth,
        float verticalTolerance = DefaultVerticalTolerance,
        float minimumHorizontalOverlap = DefaultMinimumHorizontalOverlap)
    {
        TrainItemWakeNode[] nodes = Eligible(candidates, trainCarNetId);
        Dictionary<ushort, TrainItemWakeNode> byId = nodes.ToDictionary(node => node.ItemNetId);
        List<ushort> result = new(Math.Max(0, Math.Min(maximumItems, nodes.Length)));
        Queue<(TrainItemWakeBounds Bounds, int Depth)> frontier = new();
        foreach (ushort seed in seedItemNetIds ?? Array.Empty<ushort>())
        {
            if (seed == 0 || result.Count >= maximumItems || result.Contains(seed) ||
                !byId.TryGetValue(seed, out TrainItemWakeNode node))
                continue;
            result.Add(seed);
            frontier.Enqueue((node.Bounds, 0));
        }
        Propagate(nodes, frontier, result, maximumItems, maximumDepth,
            verticalTolerance, minimumHorizontalOverlap);
        return result.ToArray();
    }

    public static bool IsSupportedBy(TrainItemWakeBounds candidate,
        TrainItemWakeBounds possibleSupport,
        float verticalTolerance = DefaultVerticalTolerance,
        float minimumHorizontalOverlap = DefaultMinimumHorizontalOverlap)
    {
        float verticalGap = candidate.MinY - possibleSupport.MaxY;
        if (verticalGap < -verticalTolerance || verticalGap > verticalTolerance)
            return false;
        float overlapX = Math.Min(candidate.MaxX, possibleSupport.MaxX) -
                         Math.Max(candidate.MinX, possibleSupport.MinX);
        float overlapZ = Math.Min(candidate.MaxZ, possibleSupport.MaxZ) -
                         Math.Max(candidate.MinZ, possibleSupport.MinZ);
        return overlapX >= minimumHorizontalOverlap && overlapZ >= minimumHorizontalOverlap;
    }

    private static TrainItemWakeNode[] Eligible(IEnumerable<TrainItemWakeNode> candidates,
        ushort trainCarNetId) => (candidates ?? Array.Empty<TrainItemWakeNode>())
        .Where(node => node.Settled && node.ItemNetId != 0 &&
                       node.TrainCarNetId == trainCarNetId)
        .OrderBy(node => node.Bounds.MinY)
        .ThenBy(node => node.ItemNetId)
        .ToArray();

    private static void Propagate(TrainItemWakeNode[] nodes,
        Queue<(TrainItemWakeBounds Bounds, int Depth)> frontier, List<ushort> result,
        int maximumItems, int maximumDepth, float verticalTolerance,
        float minimumHorizontalOverlap)
    {
        maximumItems = Math.Max(0, maximumItems);
        maximumDepth = Math.Max(0, maximumDepth);
        HashSet<ushort> included = new(result);
        while (frontier.Count > 0 && result.Count < maximumItems)
        {
            (TrainItemWakeBounds support, int depth) = frontier.Dequeue();
            if (depth >= maximumDepth)
                continue;
            foreach (TrainItemWakeNode node in nodes)
            {
                if (included.Contains(node.ItemNetId) ||
                    !IsSupportedBy(node.Bounds, support, verticalTolerance,
                        minimumHorizontalOverlap))
                    continue;
                included.Add(node.ItemNetId);
                result.Add(node.ItemNetId);
                frontier.Enqueue((node.Bounds, depth + 1));
                if (result.Count >= maximumItems)
                    break;
            }
        }
    }
}
