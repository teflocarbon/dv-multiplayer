// Either side: what the comms radio's work-train summon does (CommsRadioCrewVehiclePatch): an RPC
// ticket, then the request. livery=auto picks a livery the summon spawns for free: no car of it in
// the world, and no garage (a career can have every loco's garage, which charges and summons the
// garage's own car). Locos first, then anything else; no longer than the `fits` livery, whose car
// stood where the summon goes. Returns the answer, how long it took, and the livery.
string liveryId = ctx.Args["livery"];
if (liveryId == "auto")
{
    var present = new HashSet<string>(UnityEngine.Object.FindObjectsOfType<TrainCar>().Select(c => c.carLivery.id));
    var lengths = CarSpawner.Instance.carLiveryToCarLength;
    var fits = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == ctx.Args["fits"]);
    float room = fits != null && lengths.TryGetValue(fits, out float fitsLength) ? fitsLength : float.MaxValue;
    var pick = DV.Globals.G.Types.Liveries
        .Where(l => l.prefab != null && !l.isHidden && !present.Contains(l.id) &&
                    !global::GarageCarSpawner.Spawners.ContainsKey(l) &&
                    lengths.TryGetValue(l, out float length) && length <= room)
        .OrderByDescending(l => DV.ThingTypes.CarTypes.IsLocomotive(l))
        .ThenBy(l => lengths[l])
        .FirstOrDefault()
        ?? throw new Exception($"no livery to summon: every one no longer than {ctx.Args["fits"]} has a car in the world or a garage");
    liveryId = pick.id;
}
string response = null;
float t0 = Time.realtimeSinceStartup;
var ticket = Multiplayer.Client.Session.RpcManager.Instance.CreateTicket(float.Parse(ctx.Args["timeout"]))
    .OnResolve(r => response = (r as Multiplayer.Shared.Protocol.Data.RPCs.SpawnResponse)?.Response.ToString() ?? r.GetType().Name)
    .OnTimeout(() => response = "timeout");
Multiplayer.Client.Session.NetworkClient.Instance.SendWorkTrainRequest(ticket.TicketId, liveryId,
    ushort.Parse(ctx.Args["track"]), int.Parse(ctx.Args["index"]), true);
while (response == null)
    yield return null;
ctx.Result = new { response, seconds = Math.Round(Time.realtimeSinceStartup - t0, 2), livery = liveryId };
