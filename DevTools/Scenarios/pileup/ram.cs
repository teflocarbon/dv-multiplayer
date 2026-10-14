// Host: releases every handbrake and drives each loco into its row at SPEED m/s, all at once,
// letting it coast from 3 m out. Then waits up to 30 s for everything to stop.
// Args: cars (all ids), rams ("loco:target,..."), speed (m/s).
TrainCar Find(string id) { try { return Trains.Find(id); } catch { return null; } }
float speed = float.Parse(ctx.Args["speed"]);
var cars = ctx.Args["cars"].Split(',').Select(id => Find(id)).Where(x => x != null).ToList();
var rams = ctx.Args["rams"].Split(',').Select(p => p.Split(':')).Select(p => (loco: Find(p[0]), target: Find(p[1])))
    .Where(r => r.loco != null && r.target != null).ToList();
foreach (var car in cars) car.brakeSystem.SetHandbrakePosition(0f);

var driving = new HashSet<TrainCar>(rams.Select(r => r.loco));
var hitSpeeds = new List<float>();
for (float t0 = Time.time; Time.time - t0 < 30f && driving.Count > 0;)
{
    yield return new WaitForFixedUpdate();
    foreach (var (loco, target) in rams)
    {
        if (!driving.Contains(loco)) continue;
        float gap = Vector3.Distance(loco.transform.position, target.transform.position) - 13.5f;
        if (gap < 3f || loco.derailed) { hitSpeeds.Add(Mathf.Abs(loco.GetForwardSpeed())); driving.Remove(loco); continue; }
        Vector3 toward = (target.transform.position - loco.transform.position).normalized;
        Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, toward));
        loco.rb.AddForce(dir * speed - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
    }
}
float maxSpeed = 0f;
for (float t0 = Time.time; Time.time - t0 < 30f;)
{
    yield return new WaitForSeconds(0.5f);
    float fastest = cars.Where(x => x != null).Select(x => x.derailed ? x.rb.velocity.magnitude : Mathf.Abs(x.GetForwardSpeed())).DefaultIfEmpty(0f).Max();
    maxSpeed = Mathf.Max(maxSpeed, fastest);
    if (fastest < 0.05f && Time.time - t0 > 3f) break;
}
ctx.Result = new
{
    hitSpeeds = string.Join(",", hitSpeeds.Select(s => s.ToString("F1"))),
    maxSpeed = Math.Round(maxSpeed, 1),
    derailed = cars.Count(x => x != null && x.derailed),
    coupled = cars.Count(x => x != null && (x.frontCoupler.coupledTo != null || x.rearCoupler.coupledTo != null))
};
