// Host: spawn a loco and a wagon on the track near the client, coupled the way a player leaves
// them (chain tight, hoses connected, cocks open). Returns their ids and the coupling side.
var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.Player.NetworkedPlayer>().First();
DV.ThingTypes.TrainCarLivery L(string id) => DV.Globals.G.Types.Liveries.First(l => l.id == id);
RailTrack track = null;
foreach (float d in new[] { 15f, 30f, 45f, 60f, 80f })
foreach (var dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
{
    var t = CarSpawner.GetTrackClosestTo(client.transform.position + dir * d, 40f, out int _);
    if (t == null) continue;
    var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<DV.ThingTypes.TrainCarLivery> { L("LocoDE2"), L(ctx.Args["wagon"]) }, new List<bool> { false, false }, t, true, false);
    if (cars == null) continue;
    for (int i = 0; i < 60; i++) yield return null;
    var a = cars[0]; var b = cars[1];
    Coupler ca = null, cb = null;
    foreach (var x in new[] { a.frontCoupler, a.rearCoupler }) foreach (var y in new[] { b.frontCoupler, b.rearCoupler })
        if (ca == null || Vector3.Distance(x.transform.position, y.transform.position) < Vector3.Distance(ca.transform.position, cb.transform.position)) { ca = x; cb = y; }
    if (ca.coupledTo == null) ca.CoupleTo(cb, false, false);
    ca.ConnectAirHose(cb, false); ca.IsCockOpen = true; cb.IsCockOpen = true;
    foreach (var c in new[] { a, b }) c.brakeSystem.SetHandbrakePosition(1f); // no air yet: without handbrakes the pair can roll away
    for (int i = 0; i < 60; i++) yield return null;
    ctx.Result = new { loco = a.ID, wagon = b.ID, locoFront = ca.isFrontCoupler, wagonFront = cb.isFrontCoupler };
    yield break;
}
throw new Exception("no room to spawn the pair");
