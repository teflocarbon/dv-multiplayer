// Sample the loco's and wagon's along-track offsets from where they start (args: seconds).
var a = Trains.Find(ctx.Args["loco"]); var b = Trains.Find(ctx.Args["wagon"]);
Vector3 a0 = Dev.Abs(a.transform.position), b0 = Dev.Abs(b.transform.position);
var s = new List<string>();
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < float.Parse(ctx.Args["seconds"]);)
{
    for (float t1 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t1 < 0.5f;) yield return null;
    s.Add($"{(Dev.Abs(a.transform.position) - a0).magnitude:F1}/{(Dev.Abs(b.transform.position) - b0).magnitude:F1}(v{b.rb.velocity.magnitude:F1},hb{b.brakeSystem.handbrakePosition:F1},c{b.brakeSystem.brakeCylinderPressure:F1})");
}
ctx.Result = new { samples = string.Join(" ", s) };
