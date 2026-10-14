// Host: a loco and a wagon near the client, uncoupled, the loco backed off GAP metres from the
// wagon. Handbrakes on. Args: gap (default 12).
var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.Player.NetworkedPlayer>().First();
DV.ThingTypes.TrainCarLivery L(string id) => DV.Globals.G.Types.Liveries.First(l => l.id == id);
float gap = ctx.Args.TryGetValue("gap", out var g) ? float.Parse(g) : 12f;
foreach (float d in new[] { 20f, 40f, 60f, 80f })
foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
{
    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
    if (t == null) continue;
    var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<DV.ThingTypes.TrainCarLivery> { L("LocoDE2"), L(ctx.Args.TryGetValue("wagon", out var w) ? w : "BoxcarBrown") }, new List<bool> { false, false }, t, true, false);
    if (cars == null) continue;
    for (int i = 0; i < 60; i++) yield return null;
    var loco = cars[0]; var wagon = cars[1];
    Coupler ca = null, cb = null;
    foreach (var x in new[] { loco.frontCoupler, loco.rearCoupler }) foreach (var y in new[] { wagon.frontCoupler, wagon.rearCoupler })
        if (ca == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(ca.transform.position, cb.transform.position)) { ca = x; cb = y; }
    if (ca.coupledTo != null) ca.Uncouple(false, false, false, false);
    wagon.brakeSystem.SetHandbrakePosition(1f);
    loco.brakeSystem.SetHandbrakePosition(0f);
    // back the loco off
    for (float t0 = Time.time; Time.time - t0 < 20f && Vector3.Distance(ca.transform.position, cb.transform.position) < gap;)
    {
        yield return new WaitForFixedUpdate();
        Vector3 away = (loco.transform.position - wagon.transform.position).normalized;
        Vector3 dirL = loco.transform.forward * Mathf.Sign(Vector3.Dot(loco.transform.forward, away));
        loco.rb.AddForce(dirL * 3f - Vector3.Project(loco.rb.velocity, dirL), ForceMode.VelocityChange);
    }
    for (int i = 0; i < 100; i++) { yield return new WaitForFixedUpdate(); loco.rb.velocity = Vector3.zero; }
    loco.brakeSystem.SetHandbrakePosition(1f);
    for (int i = 0; i < 60; i++) yield return null;
    ctx.Result = new { loco = loco.ID, wagon = wagon.ID, locoFront = ca.isFrontCoupler, wagonFront = cb.isFrontCoupler, gap = Math.Round(Vector3.Distance(ca.transform.position, cb.transform.position), 1) };
    yield break;
}
throw new Exception("no room to spawn the train");
