// Either side: what the comms radio's work-train summon does (CommsRadioCrewVehiclePatch): an RPC
// ticket, then the request. livery=auto picks a loco with no car in the world (so the summon spawns
// one) and no garage (so it's free). Returns the answer, how long it took, and the livery.
string liveryId = ctx.Args["livery"];
if (liveryId == "auto")
{
    var present = new HashSet<string>(UnityEngine.Object.FindObjectsOfType<TrainCar>().Select(c => c.carLivery.id));
    liveryId = new[] { "LocoDE2", "LocoDM3", "LocoDH4", "LocoS060", "LocoDE6" }
        .First(id => !present.Contains(id) && !global::GarageCarSpawner.Spawners.Keys.Any(l => l.id == id));
}
string response = null;
float t0 = Time.realtimeSinceStartup;
var ticket = Multiplayer.Client.Session.RpcManager.Instance.CreateTicket(float.Parse(ctx.Args["timeout"]))
    .OnResolve(r => response = (r as Multiplayer.Shared.Protocol.Data.RPCs.SpawnResponse)?.Response.ToString() ?? r.GetType().Name)
    .OnTimeout(() => response = "timeout");
Multiplayer.Shared.Session.NetworkLifecycle.Instance.Client.SendWorkTrainRequest(ticket.TicketId, liveryId,
    ushort.Parse(ctx.Args["track"]), int.Parse(ctx.Args["index"]), true);
while (response == null)
    yield return null;
ctx.Result = new { response, seconds = Math.Round(Time.realtimeSinceStartup - t0, 2), livery = liveryId };
