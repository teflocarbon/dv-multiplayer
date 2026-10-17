// A car's physical state: derailed, kinematic (DV freezes a derailed car whose world isn't
// loaded), whether its world cell is loaded, whether there's ground under it, its speed, and its
// absolute position. Args: car (car ID).
var car = Trains.Find(ctx.Args["car"]);
var streaming = UnityEngine.Object.FindObjectOfType<WorldStreamingInit>();
var below = Physics.RaycastAll(car.transform.position + Vector3.up * 3f, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore)
    .Where(h => h.collider.attachedRigidbody != car.rb && !h.transform.IsChildOf(car.transform))
    .OrderBy(h => h.distance).ToList();
var abs = Dev.Abs(car.transform.position);
return new
{
    derailed = car.derailed,
    kinematic = car.rb.isKinematic,
    loaded = streaming != null && streaming.IsSceneAndTerrainCellLoaded(car.transform.position),
    ground = below.Count > 0,
    groundCollider = below.Count > 0 ? below[0].collider.GetType().Name + " " + below[0].collider.name : null,
    speed = Math.Round(car.rb.velocity.magnitude, 2),
    abs = new { x = abs.x, y = abs.y, z = abs.z },
    fromHostBody = PlayerManager.PlayerTransform == null ? -1 : Math.Round(Vector3.Distance(car.transform.position, PlayerManager.PlayerTransform.position)),
};
