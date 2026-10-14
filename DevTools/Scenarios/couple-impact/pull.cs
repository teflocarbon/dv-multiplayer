// Host: pull the loco away from the wagon for a few seconds and stop. Args: loco, wagon.
var loco = Trains.Find(ctx.Args["loco"]); var wagon = Trains.Find(ctx.Args["wagon"]);
foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(0f);
if (loco.trainset == wagon.trainset) wagon.brakeSystem.SetHandbrakePosition(0f);
for (float t0 = Time.time; Time.time - t0 < 5f;)
{
    yield return new WaitForFixedUpdate();
    Vector3 away = (loco.transform.position - wagon.transform.position).normalized;
    Vector3 dir = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, away));
    loco.rb.AddForce(dir * 2f * Mathf.Clamp01((Time.time - t0) / 2f) - Vector3.Project(loco.rb.velocity, dir), ForceMode.VelocityChange);
}
for (int i = 0; i < 100; i++) { yield return new WaitForFixedUpdate(); foreach (var c in loco.trainset.cars) c.rb.velocity = Vector3.zero; }
foreach (var c in loco.trainset.cars) c.brakeSystem.SetHandbrakePosition(1f);
wagon.brakeSystem.SetHandbrakePosition(1f);
ctx.Result = new { together = loco.trainset == wagon.trainset };
