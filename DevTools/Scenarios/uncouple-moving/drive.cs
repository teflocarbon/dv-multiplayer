// Host: drive the loco's current trainset at speed, uncouple the last wagon partway, keep driving
// the front part, then stop. Args: loco, last, lastFront, speed, at (seconds before uncoupling).
var loco = Trains.Find(ctx.Args["loco"]); var last = Trains.Find(ctx.Args["last"]);
Coupler link = bool.Parse(ctx.Args["lastFront"]) ? last.frontCoupler : last.rearCoupler;
float speed = float.Parse(ctx.Args["speed"]), at = float.Parse(ctx.Args["at"]);
foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
Vector3 fwd = loco.transform.forward;
// Drive away from the wagons.
if (Vector3.Dot(fwd, last.transform.position - loco.transform.position) > 0) fwd = -fwd;
bool uncoupled = false;
float t0 = Time.time;
while (Time.time - t0 < at + 6f)
{
    yield return new WaitForFixedUpdate();
    float t = Time.time - t0;
    if (!uncoupled && t >= at) { link.Uncouple(true, false, false, false); uncoupled = true; }
    float v = speed * Mathf.Clamp01(t / 2f);
    foreach (var c in loco.trainset.cars)
    {
        Vector3 dir = c.transform.forward * Mathf.Sign(Vector3.Dot(c.transform.forward, fwd));
        c.rb.AddForce(dir * v - Vector3.Project(c.rb.velocity, dir), ForceMode.VelocityChange);
    }
}
foreach (var c in loco.trainset.cars) { c.rb.velocity = Vector3.zero; c.brakeSystem.SetHandbrakePosition(1f); }
ctx.Result = new { uncoupled, lastSetSize = last.trainset.cars.Count };
