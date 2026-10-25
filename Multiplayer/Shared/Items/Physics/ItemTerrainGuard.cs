using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Physics;

/// <summary>Puts an item this game simulates back on the terrain the physics step it went through it.</summary>
internal sealed class ItemTerrainGuard
{
    // Terrain tunnelling guard. A small item with a convex mesh collider (a mug) can go straight
    // through DV's terrain heightfield with no contact at all, in plain PhysX: near the track
    // embankment 14 of 20 throws at about 10 m/s (docs/research/item-terrain-tunnelling.md). The
    // fall rescue catches it a second later, but puts it back at its last safe spot, which for a
    // throw is by the thrower's hand. So right after each physics step, an item this game simulates
    // that went from above the terrain to under it in that step is put back on the surface where it
    // crossed, without the velocity that took it in.
    //
    // It runs on every peer, on the bodies that peer simulates: a lease's simulator, or the host
    // for a world item with no lease. A proxy is kinematic and is skipped. The terrain collider
    // itself says whether there was a surface to cross; DV's tunnel portal holes are only visual,
    // so items near a portal (TunnelCollisionIgnore) or with the terrain ignored are left alone, as
    // are items riding a train and items moved by code rather than by their velocity.

    internal static bool Enabled = true;   // off only to measure vanilla behaviour
    internal static int Catches;

    private const float TunnelPortalClearance = 10f;   // metres from a portal's trigger
    private const float TerrainGuardLift = 0.02f;      // metres between the item and the surface

    private readonly NetworkedItemManager manager;
    private static readonly WaitForFixedUpdate AfterPhysicsStep = new();
    private readonly Dictionary<NetworkedItem, AboveTerrain> lastAboveTerrain = new();
    private readonly Collider[] portalOverlap = new Collider[128];
    private readonly RaycastHit[] terrainCrossings = new RaycastHit[16];
    private int terrainGuardStep;

    internal ItemTerrainGuard(NetworkedItemManager manager) => this.manager = manager;

    private struct AboveTerrain
    {
        public Vector3 AbsolutePosition;
        public int Step;
    }

    // WaitForFixedUpdate resumes after the physics step and its collision callbacks, so a crossing
    // is undone before anything renders or samples it.
    internal IEnumerator Run()
    {
        while (true)
        {
            yield return AfterPhysicsStep;
            terrainGuardStep++;
            if (Enabled)
                GuardItemsAgainstTerrain();
        }
    }

    private void GuardItemsAgainstTerrain()
    {
        if (terrainGuardStep % 600 == 0)
            foreach (NetworkedItem gone in lastAboveTerrain.Keys.Where(item => item == null).ToList())
                lastAboveTerrain.Remove(gone);

        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            Rigidbody body = item != null ? item.Item?.ItemRigidbody : null;
            if (body == null || body.isKinematic || body.IsSleeping() || !item.gameObject.activeInHierarchy)
                continue;
            if (ItemFallRescue.TerrainUnder(body.position) is not Terrain terrain)
                continue;

            Vector3 absolute = body.position - WorldMover.currentMove;
            float surfaceWorldY = terrain.SampleHeight(body.position) + terrain.transform.position.y;
            if (body.position.y >= surfaceWorldY)
            {
                lastAboveTerrain[item] = new AboveTerrain { AbsolutePosition = absolute, Step = terrainGuardStep };
                continue;
            }
            if (!lastAboveTerrain.TryGetValue(item, out AboveTerrain above) || above.Step != terrainGuardStep - 1)
                continue;
            lastAboveTerrain.Remove(item);
            TryPutBackOnTerrain(item, body, above.AbsolutePosition + WorldMover.currentMove);
        }
    }

    private void TryPutBackOnTerrain(NetworkedItem item, Rigidbody body, Vector3 from)
    {
        // Moved by its velocity, not put somewhere by code (a teleport, a snapshot, a placement).
        Vector3 moved = body.position - from;
        if (moved.magnitude > body.velocity.magnitude * Time.fixedDeltaTime * 2f + 0.25f)
            return;
        if (item.Item.IsGrabbed() || item.TryGetPhysicalTrainParent(out _))
            return;

        // SampleHeight interpolates the rendered heightmap; the collider is the truth, and it's a
        // separate object (TerrainGrid's pool, or the host's extra terrain around remote players).
        // No hit: it didn't really go under the surface the physics has.
        if (!TryCrossTerrainCollider(from, moved, out RaycastHit hit))
            return;
        TerrainCollider terrainCollider = (TerrainCollider)hit.collider;

        Collider[] colliders = item.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
        if (colliders.Length == 0 || colliders.Any(c => UnityEngine.Physics.GetIgnoreCollision(terrainCollider, c)) ||
            NearTunnelPortal(hit.point))
            return;

        Bounds bounds = colliders[0].bounds;
        foreach (Collider c in colliders)
            bounds.Encapsulate(c.bounds);
        Vector3 target = hit.point + Vector3.up * (body.position.y - bounds.min.y + TerrainGuardLift);

        Vector3 velocity = body.velocity;
        float into = Vector3.Dot(velocity, hit.normal);
        if (into < 0f)
            velocity -= hit.normal * into;

        Vector3 under = body.position;
        body.position = target;
        item.transform.position = target;
        body.velocity = velocity;
        Catches++;

        Multiplayer.LogDebug(() => $"Terrain guard: {item.name} ({item.NetId}) went through the terrain at " +
            $"{hit.point - WorldMover.currentMove}, put back on it ({under.y - hit.point.y:0.00} m under)");
        NetworkedItemSpatialManager.Publish("terrain-guard", item.NetId, new Dictionary<string, object>
        {
            ["crossedAt"] = DebugValueSnapshotter.Snapshot(hit.point - WorldMover.currentMove),
            ["depth"] = hit.point.y - under.y,
            ["speed"] = moved.magnitude / Time.fixedDeltaTime
        }, manager.ServerPart != null ? DebugRuntimeSide.Server : DebugRuntimeSide.Client);
    }

    private bool TryCrossTerrainCollider(Vector3 from, Vector3 moved, out RaycastHit crossing)
    {
        crossing = default;
        float distance = moved.magnitude;
        if (distance < 1e-4f)
            return false;
        int count = UnityEngine.Physics.RaycastNonAlloc(new Ray(from, moved), terrainCrossings, distance + 0.01f,
            UnityEngine.Physics.AllLayers, QueryTriggerInteraction.Ignore);
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            if (terrainCrossings[i].collider is not TerrainCollider || (found && terrainCrossings[i].distance >= crossing.distance))
                continue;
            crossing = terrainCrossings[i];
            found = true;
        }
        return found;
    }

    // DV cuts the terrain open at a tunnel portal only visually: TunnelCollisionIgnore makes what
    // enters the portal's trigger ignore the terrain collider. The guard stays out of its way.
    private bool NearTunnelPortal(Vector3 worldPosition)
    {
        int count = UnityEngine.Physics.OverlapSphereNonAlloc(worldPosition, TunnelPortalClearance, portalOverlap,
            UnityEngine.Physics.AllLayers, QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; i++)
            if (portalOverlap[i] != null && portalOverlap[i].GetComponentInParent<TunnelCollisionIgnore>() != null)
                return true;
        return false;
    }
}
