// Host: moves a derailed car sideways off the track, to the nearest spot (15-60 m either side)
// with no static ground under its middle or either bogie on the host: off the ballast, where a
// server with no terrain loaded there has nothing to hold it. Result: whether a spot was found,
// how far it moved, and the car's height there.
var car = Trains.Find(ctx.Args["car"]);
bool Ground(Vector3 p) => Physics.RaycastAll(p + Vector3.up * 3f, Vector3.down, 40f, ~0, QueryTriggerInteraction.Ignore)
    .Any(h => h.collider.attachedRigidbody == null);
var points = new[] { car.transform.position }.Concat(car.Bogies.Select(b => b.transform.position)).ToList();
var offsets = new[] { 15f, 20f, 30f, 40f, 60f }.SelectMany(d => new[] { d, -d })
    .Select(d => car.transform.right * d + Vector3.up)
    .Where(o => !points.Any(p => Ground(p + o)))
    .ToList();
if (offsets.Count == 0)
    return new { found = false };
var offset = offsets[0];
car.rb.velocity = Vector3.zero;
car.rb.angularVelocity = Vector3.zero;
car.transform.position += offset;
Physics.SyncTransforms();
var abs = Dev.Abs(car.transform.position);
return new { found = true, moved = Math.Round(offset.magnitude, 1), y = abs.y };
