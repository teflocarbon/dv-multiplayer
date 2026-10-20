// Client: record the last wagon's absolute position every physics step for a while (args: car,
// seconds) and report every single-step jump over 1 m with when it happened (utc, ms since the
// Unix epoch, to compare with the host's stall: both games run on one machine's clock), plus the
// biggest of them.
var car = Trains.Find(ctx.Args["car"]);
float seconds = float.Parse(ctx.Args["seconds"]);
var jumps = new List<object>();
float worstJump = 0, worstAt = 0;
Vector3 lastP = Dev.Abs(car.transform.position);
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
{
    yield return new WaitForFixedUpdate();
    Vector3 now = Dev.Abs(car.transform.position);
    float jump = Vector3.Distance(now, lastP);
    float t = Time.realtimeSinceStartup - t0;
    if (jump > worstJump) { worstJump = jump; worstAt = t; }
    if (jump > 1f && jumps.Count < 50)
        jumps.Add(new
        {
            utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            t = Math.Round(t, 1),
            jump = Math.Round(jump, 2),
            note = $"derailed {car.derailed}, kinematic {car.rb.isKinematic}, v {car.GetForwardSpeed():F1}, bogies {string.Join("/", car.Bogies.Select(b => (b.track?.name ?? "-") + (b.HasDerailed ? "!" : "")))}, frame {Time.unscaledDeltaTime * 1000f:F0} ms",
        });
    lastP = now;
}
ctx.Result = new { jumps, worstJump = Math.Round(worstJump, 2), worstAt = Math.Round(worstAt, 1) };
