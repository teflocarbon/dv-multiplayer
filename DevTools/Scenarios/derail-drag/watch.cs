// Either peer: while the train moves, samples every physics step the gap between the loco's coupler
// and the derailed car's (the pair coupled to each other). Dragged smoothly, the gap barely changes;
// a judder (the derailed car yanked back and forth against its couplers) makes it swing every step.
// Returns how fast the gap changes (RMS and the worst step, in m/s), its range, and the end state,
// and how much the loco's and the derailed car's own velocities change each step (RMS, m/s^2): which
// of the two shakes, when the gap does.
// Args: loco, car (the derailed one), seconds, settle (seconds skipped while the train gets going).
var loco = Trains.Find(ctx.Args["loco"]); var car = Trains.Find(ctx.Args["car"]);
float seconds = float.Parse(ctx.Args["seconds"]), settle = float.Parse(ctx.Args["settle"]);
Coupler a = null, b = null;
foreach (var x in new[] { loco.frontCoupler, loco.rearCoupler })
foreach (var y in new[] { car.frontCoupler, car.rearCoupler })
    if (a == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(a.transform.position, b.transform.position)) { a = x; b = y; }
float t0 = Time.time, prev = float.NaN, sumSq = 0f, worst = 0f, min = float.MaxValue, max = float.MinValue, speedSum = 0f;
int n = 0;
Vector3 lastPos = loco.transform.position;
Vector3 locoV = loco.rb.velocity, carV = car.rb.velocity;
float locoSq = 0f, carSq = 0f;
while (Time.time - t0 < seconds)
{
    yield return new WaitForFixedUpdate();
    float gap = Vector3.Distance(a.transform.position, b.transform.position);
    float moved = Vector3.Distance(loco.transform.position, lastPos);
    lastPos = loco.transform.position;
    if (Time.time - t0 < settle || moved > 5f) { prev = gap; continue; }   // > 5 m in a step: a world shift
    if (!float.IsNaN(prev))
    {
        float rate = Mathf.Abs(gap - prev) / Time.fixedDeltaTime;
        sumSq += rate * rate; worst = Mathf.Max(worst, rate); n++;
        speedSum += moved / Time.fixedDeltaTime;
        locoSq += ((loco.rb.velocity - locoV) / Time.fixedDeltaTime).sqrMagnitude;
        carSq += ((car.rb.velocity - carV) / Time.fixedDeltaTime).sqrMagnitude;
    }
    locoV = loco.rb.velocity; carV = car.rb.velocity;
    min = Mathf.Min(min, gap); max = Mathf.Max(max, gap);
    prev = gap;
}
ctx.Result = new
{
    samples = n,
    rms = n > 0 ? Math.Round(Mathf.Sqrt(sumSq / n), 3) : -1,
    worst = Math.Round(worst, 2),
    gapRange = n > 0 ? Math.Round(max - min, 3) : -1,
    locoAccel = n > 0 ? Math.Round(Mathf.Sqrt(locoSq / n), 2) : -1,
    carAccel = n > 0 ? Math.Round(Mathf.Sqrt(carSq / n), 2) : -1,
    speedKmh = n > 0 ? Math.Round(speedSum / n * 3.6f, 1) : 0,
    derailed = car.derailed,
    coupled = car.trainset == loco.trainset,
};
