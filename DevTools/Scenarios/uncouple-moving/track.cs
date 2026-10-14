// Record the last wagon's absolute position every physics step for a while (args: car, seconds);
// report the biggest single-step jump and its final position.
var car = Trains.Find(ctx.Args["car"]);
float seconds = float.Parse(ctx.Args["seconds"]);
var pts = new List<(float t, Vector3 p)>();
var notes = new List<string>();
Vector3 lastP = Dev.Abs(car.transform.position);
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
{
    yield return new WaitForFixedUpdate();
    Vector3 now = Dev.Abs(car.transform.position);
    if (Vector3.Distance(now, lastP) > 1f && notes.Count < 5)
        notes.Add($"{Time.realtimeSinceStartup - t0:F1}s jump {Vector3.Distance(now, lastP):F1} m: derailed {car.derailed}, kinematic {car.rb.isKinematic}, v {car.GetForwardSpeed():F1}, bogies {string.Join("/", car.Bogies.Select(b => (b.track?.name ?? "-") + (b.HasDerailed ? "!" : "")))}, frame {Time.unscaledDeltaTime * 1000f:F0} ms");
    lastP = now;
    pts.Add((Time.realtimeSinceStartup - t0, now));
}
float worstJump = 0, worstAt = 0;
for (int i = 1; i < pts.Count; i++)
{
    float dt = Mathf.Max(pts[i].t - pts[i - 1].t, 0.001f);
    float jump = Vector3.Distance(pts[i].p, pts[i - 1].p);
    // a jump well beyond what the speed allows over that step is a snap
    if (jump > worstJump) { worstJump = jump; worstAt = pts[i].t; }
}
float travelled = Vector3.Distance(pts.First().p, pts.Last().p);
// furthest the wagon got from where it ends: a wagon dragged along and snapped back shows here
float overshoot = pts.Max(p => Vector3.Distance(p.p, pts.Last().p)) - Vector3.Distance(pts.First().p, pts.Last().p);
ctx.Result = new { notes, worstJump = Math.Round(worstJump, 2), worstAt = Math.Round(worstAt, 1), travelled = Math.Round(travelled, 1), overshoot = Math.Round(overshoot, 2), final = pts.Last().p };
