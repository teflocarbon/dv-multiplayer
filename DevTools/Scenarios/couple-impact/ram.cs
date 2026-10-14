// Host: run the loco into the wagon at SPEED m/s and coast from 3 m out (a driver who left it a
// bit late). Wagon handbrake (arg) from 5 m out. Args: loco, wagon, locoFront, wagonFront, speed, wagonBrake (0/1).
var loco = Trains.Find(ctx.Args["loco"]); var wagon = Trains.Find(ctx.Args["wagon"]);
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? loco.frontCoupler : loco.rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? wagon.frontCoupler : wagon.rearCoupler;
float speed = float.Parse(ctx.Args["speed"]);
loco.brakeSystem.SetHandbrakePosition(0f);
float minGap = 99f, hitSpeed = 0f;
for (float t0 = Time.time; Time.time - t0 < 25f;)
{
    yield return new WaitForFixedUpdate();
    float gap = Vector3.Distance(ca.transform.position, cb.transform.position);
    minGap = Mathf.Min(minGap, gap);
    // a free wagon on a grade would roll off before the loco got there: let it go just before the hit
    if (gap < 5f) wagon.brakeSystem.SetHandbrakePosition(float.Parse(ctx.Args["wagonBrake"]));
    if (gap < 3f) { hitSpeed = Mathf.Max(hitSpeed, Mathf.Abs(loco.GetForwardSpeed())); break; }
    Vector3 toward = (wagon.transform.position - loco.transform.position).normalized;
    Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, toward));
    loco.rb.AddForce(dir * speed - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
}
for (float t0 = Time.time; Time.time - t0 < 8f;) { yield return new WaitForFixedUpdate(); minGap = Mathf.Min(minGap, Vector3.Distance(ca.transform.position, cb.transform.position)); }
loco.brakeSystem.SetHandbrakePosition(1f);
ctx.Result = new { hitSpeed = Math.Round(hitSpeed, 2), minGap = Math.Round(minGap, 2), coupled = ca.coupledTo == cb };
