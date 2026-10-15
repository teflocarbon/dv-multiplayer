// Watch the loco and wagon every physics step for a while (args: loco, wagon, locoFront,
// wagonFront, seconds): the fastest either moved or spun, the biggest single-step jump, and when
// the coupling between them changed. A desynced joint fighting the corrections shows as speeds
// and spins far beyond what the host's train does.
var cars = new[] { Trains.Find(ctx.Args["loco"]), Trains.Find(ctx.Args["wagon"]) };
Coupler ca = bool.Parse(ctx.Args["locoFront"]) ? cars[0].frontCoupler : cars[0].rearCoupler;
Coupler cb = bool.Parse(ctx.Args["wagonFront"]) ? cars[1].frontCoupler : cars[1].rearCoupler;
float seconds = float.Parse(ctx.Args["seconds"]);
float maxSpeed = 0, maxSpin = 0, worstJump = 0, maxGap = 0;
string maxSpeedCar = "";
var events = new List<string>();
var last = cars.Select(c => Dev.Abs(c.transform.position)).ToArray();
bool coupled = ca.coupledTo == cb, coupledAtStart = coupled;
float t0 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - t0 < seconds)
{
    yield return new WaitForFixedUpdate();
    float t = Time.realtimeSinceStartup - t0;
    for (int i = 0; i < cars.Length; i++)
    {
        var c = cars[i];
        float v = c.rb.velocity.magnitude, w = c.rb.angularVelocity.magnitude * Mathf.Rad2Deg;
        if (v > maxSpeed) { maxSpeed = v; maxSpeedCar = c.ID + " at " + t.ToString("F1") + "s"; }
        maxSpin = Mathf.Max(maxSpin, w);
        Vector3 p = Dev.Abs(c.transform.position);
        worstJump = Mathf.Max(worstJump, Vector3.Distance(p, last[i]));
        last[i] = p;
        if (c.derailed && events.Count < 20 && !events.Any(e => e.Contains(c.ID + " derailed")))
            events.Add($"{t:F1}s {c.ID} derailed");
    }
    maxGap = Mathf.Max(maxGap, Vector3.Distance(ca.transform.position, cb.transform.position));
    bool now = ca.coupledTo == cb;
    if (now != coupled && events.Count < 20)
        events.Add($"{t:F1}s {(now ? "coupled" : "uncoupled")}, couplers {Vector3.Distance(ca.transform.position, cb.transform.position):F2} m apart");
    coupled = now;
}
ctx.Result = new
{
    maxSpeed = Math.Round(maxSpeed, 2), maxSpeedCar, maxSpin = Math.Round(maxSpin, 1),
    worstJump = Math.Round(worstJump, 2), maxGap = Math.Round(maxGap, 2), events,
    coupledAtStart, coupled, derailed = cars.Any(c => c.derailed)
};
