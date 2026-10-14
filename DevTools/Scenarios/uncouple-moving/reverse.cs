// Host: reverse the loco's part back into the stopped wagon (a shunting shove; DV does not couple on contact).
var loco = Trains.Find(ctx.Args["loco"]); var last = Trains.Find(ctx.Args["last"]);
foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
last.brakeSystem.SetHandbrakePosition(1f);
float t0 = Time.time;
while (Time.time - t0 < 12f)
{
    yield return new WaitForFixedUpdate();
    Vector3 toward = (last.transform.position - loco.transform.position).normalized;
    foreach (var c in loco.trainset.cars)
    {
        Vector3 dir = c.transform.forward * Mathf.Sign(Vector3.Dot(c.transform.forward, toward));
        c.rb.AddForce(dir * 1.5f - Vector3.Project(c.rb.velocity, dir), ForceMode.VelocityChange);
    }
}
for (int i = 0; i < 50; i++) { yield return new WaitForFixedUpdate(); foreach (var c in loco.trainset.cars) c.rb.velocity = Vector3.zero; }
foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(1f);
ctx.Result = new { coupled = loco.trainset == last.trainset, seconds = Math.Round(Time.time - t0, 1) };
