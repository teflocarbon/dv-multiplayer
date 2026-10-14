// Host: a DE2 and N boxcars near the client, coupled with hoses joined and cocks open, the loco
// started and the brake pipe charged. Args: wagons (default 4).
var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.Player.NetworkedPlayer>().First();
DV.ThingTypes.TrainCarLivery L(string id) => DV.Globals.G.Types.Liveries.First(l => l.id == id);
int n = ctx.Args.TryGetValue("wagons", out var w) ? int.Parse(w) : 4;
var liveries = new List<DV.ThingTypes.TrainCarLivery> { L("LocoDE2") };
for (int i = 0; i < n; i++) liveries.Add(L("BoxcarBrown"));
foreach (float d in new[] { 20f, 40f, 60f, 80f })
foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
{
    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
    if (t == null) continue;
    var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, liveries.Select(_ => false).ToList(), t, true, false);
    if (cars == null) continue;
    for (int i = 0; i < 60; i++) yield return null;
    for (int k = 0; k < cars.Count - 1; k++)
    {
        Coupler ca = null, cb = null;
        foreach (var x in new[] { cars[k].frontCoupler, cars[k].rearCoupler }) foreach (var y in new[] { cars[k + 1].frontCoupler, cars[k + 1].rearCoupler })
            if (ca == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(ca.transform.position, cb.transform.position)) { ca = x; cb = y; }
        if (ca.coupledTo == null) ca.CoupleTo(cb, false, false);
        ca.ConnectAirHose(cb, false); ca.IsCockOpen = true; cb.IsCockOpen = true;
    }
    // close the cocks at both ends of the train, or the open ends vent the brake pipe
    foreach (var c in cars) foreach (var end in new[] { c.frontCoupler, c.rearCoupler }) if (end.coupledTo == null) end.IsCockOpen = false;
    foreach (var c in cars) c.brakeSystem.SetHandbrakePosition(0f);
    yield return Trains.StartLoco(cars[0]);
    // cut in the loco's train brake valve (a spawned loco has it cut out), or nothing feeds the pipe
    cars[0].brakeSystem.trainBrakeCutout = true;
    // charge the brake pipe through the whole train
    float t0 = Time.time;
    while (Time.time - t0 < 120f && cars.Any(c => c.brakeSystem.brakePipePressure < 4.9f || c.brakeSystem.brakeCylinderPressure > 1.2f)) yield return null;
    ctx.Result = new { loco = cars[0].ID, cars = string.Join(",", cars.Select(c => c.ID)), last = cars.Last().ID, charged = cars.Min(c => c.brakeSystem.brakePipePressure), cylinders = cars.Max(c => c.brakeSystem.brakeCylinderPressure), seconds = Math.Round(Time.time - t0) };
    yield break;
}
throw new Exception("no room to spawn the train");
