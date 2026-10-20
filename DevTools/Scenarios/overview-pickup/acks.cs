// Host: the station's job overviews, which of them the client has (the server sent it the item)
// and which the server took its acknowledgement for. Waits up to 15 s for acknowledgements.
var server = Multiplayer.Server.Session.NetworkServer.Instance;
var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
var st = StationController.allStations.First(s => s.stationInfo.YardID == ctx.Args["station"]);
List<NetworkedItem> overviews = null, known = null;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 15f; )
{
    overviews = st.logicStation.availableJobs
        .Select(j => NetworkedJob.TryGetFromJob(j, out var nj) ? nj.JobOverview : null)
        .Where(o => o != null && o.GetItemState() == ItemState.Dropped).ToList();
    known = overviews.Where(o => client.KnownItems.ContainsKey(o)).ToList();
    if (known.Count > 0 && known.All(o => client.AcknowledgedWorldItems.Contains(o.NetId)))
        break;
    yield return new WaitForSecondsRealtime(0.5f);
}
var acked = known.Where(o => client.AcknowledgedWorldItems.Contains(o.NetId)).ToList();
ctx.Result = new
{
    overviews = overviews.Count,
    known = known.Count,
    acked = acked.Count,
    unacked = string.Join(",", known.Except(acked).Select(o => o.NetId)),
    knownIds = string.Join(",", known.Select(o => o.NetId))
};
