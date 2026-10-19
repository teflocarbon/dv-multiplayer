// Host: drags the train by its loco alone (the derailed car is pulled by its couplers, the last car
// rolls on the rails behind it) at `speed` m/s for `seconds`, then stops it. Args: loco, others
// (comma-separated car ids behind it), speed, seconds.
var loco = Trains.Find(ctx.Args["loco"]);
float speed = float.Parse(ctx.Args["speed"]), seconds = float.Parse(ctx.Args["seconds"]);
var behind = ctx.Args["others"].Split((char)44).Select(id => Trains.Find(id)).ToList();
foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
Vector3 fwd = loco.transform.forward;
if (Vector3.Dot(fwd, behind[0].transform.position - loco.transform.position) > 0) fwd = -fwd;
float t0 = Time.time;
while (Time.time - t0 < seconds)
{
    yield return new WaitForFixedUpdate();
    float v = speed * Mathf.Clamp01((Time.time - t0) / 3f);
    Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, fwd));
    loco.rb.AddForce(dir * v - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
}
foreach (var c in loco.trainset.cars.Concat(behind).Distinct()) { c.rb.velocity = Vector3.zero; c.brakeSystem.SetHandbrakePosition(1f); }
ctx.Result = new { setSize = loco.trainset.cars.Count };
