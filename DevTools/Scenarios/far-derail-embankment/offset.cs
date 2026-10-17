// Host: moves a derailed car sideways off the track, 3 m above the nearest open spot (15-80 m either
// side) where the host has bare terrain under the whole car: every point of a grid over its
// footprint, plus a few metres around it, hits a terrain collider first, not ballast, a platform or
// a building. Result: whether a spot was found, how far it moved, and the car's height there.
var car = Trains.Find(ctx.Args["car"]);
RaycastHit? Top(Vector3 p) { var hits = Physics.RaycastAll(p + Vector3.up * 30f, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore).Where(h => h.collider.attachedRigidbody == null).OrderBy(h => h.distance).ToList(); return hits.Count > 0 ? hits[0] : (RaycastHit?)null; }
var fwd = car.transform.forward; var right = car.transform.right;
var footprint = new List<Vector3>();
for (float a = -12f; a <= 12f; a += 4f)
    for (float b = -5f; b <= 5f; b += 2.5f)
        footprint.Add(car.transform.position + fwd * a + right * b);
foreach (float d in new[] { 15f, 20f, 30f, 40f, 60f, 80f }.SelectMany(d => new[] { d, -d }))
{
    var side = right * d;
    var tops = footprint.Select(p => Top(p + side)).ToList();
    if (tops.Any(t => t == null || !(t.Value.collider is TerrainCollider)))
        continue;
    float lift = tops.Max(t => t.Value.point.y) + 3f - car.transform.position.y;
    car.rb.velocity = Vector3.zero;
    car.rb.angularVelocity = Vector3.zero;
    car.transform.position += side + Vector3.up * lift;
    Physics.SyncTransforms();
    var abs = Dev.Abs(car.transform.position);
    return new { found = true, moved = Math.Abs(d), y = abs.y, terrain = tops[0].Value.collider.name };
}
return new { found = false };
