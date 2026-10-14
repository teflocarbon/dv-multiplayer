// Host: a loco and two wagons near the client, coupled (chain tight, hoses, cocks), handbrakes on.
var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.Player.NetworkedPlayer>().First();
DV.ThingTypes.TrainCarLivery L(string id) => DV.Globals.G.Types.Liveries.First(l => l.id == id);
foreach (float d in new[] { 20f, 40f, 60f, 80f })
foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
{
    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 60f, out int _);
    if (t == null) continue;
    var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<DV.ThingTypes.TrainCarLivery> { L("LocoDE2"), L("BoxcarBrown"), L(ctx.Args.TryGetValue("last", out var lastLivery) ? lastLivery : "BoxcarBrown") }, new List<bool> { false, false, false }, t, true, false);
    if (cars == null) continue;
    for (int i = 0; i < 60; i++) yield return null;
    for (int k = 0; k < 2; k++)
    {
        Coupler ca = null, cb = null;
        foreach (var x in new[] { cars[k].frontCoupler, cars[k].rearCoupler }) foreach (var y in new[] { cars[k + 1].frontCoupler, cars[k + 1].rearCoupler })
            if (ca == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(ca.transform.position, cb.transform.position)) { ca = x; cb = y; }
        if (ca.coupledTo == null) ca.CoupleTo(cb, false, false);
        ca.ConnectAirHose(cb, false); ca.IsCockOpen = true; cb.IsCockOpen = true;
    }
    foreach (var c in cars) c.brakeSystem.SetHandbrakePosition(1f);
    for (int i = 0; i < 60; i++) yield return null;
    var last = cars[2];
    var link = new[] { last.frontCoupler, last.rearCoupler }.First(c => c.coupledTo != null && c.coupledTo.train == cars[1]);
    ctx.Result = new { loco = cars[0].ID, mid = cars[1].ID, last = last.ID, lastFront = link.isFrontCoupler, set = cars[0].trainset == last.trainset };
    yield break;
}
throw new Exception("no room to spawn the train");
