// Host: while new job overviews appear, nudge each one the client has been sent (it's in the
// client's known items) but hasn't acknowledged yet, so its revision moves on before the client's
// acknowledgement arrives. Stands in for another game's update to a new overview. Runs until no
// new overview has turned up for 15 s (at most 90 s).
var server = NetworkLifecycle.Instance.Server;
var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
var nudged = new Dictionary<ushort, uint>();
float start = Time.realtimeSinceStartup, last = start;
while (Time.realtimeSinceStartup - start < 90f && (nudged.Count == 0 || Time.realtimeSinceStartup - last < 15f))
{
    foreach (var item in client.KnownItems.Keys.ToList())
    {
        if (item == null || nudged.ContainsKey(item.NetId) || item.GetTrackedItem<JobOverview>() == null ||
            client.AcknowledgedWorldItems.Contains(item.NetId) || !AuthoritativeItemRegistry.TryGet(item.NetId, out var record))
            continue;
        nudged[item.NetId] = record.Revision;
        item.transform.position += Vector3.up * 0.05f;
        last = Time.realtimeSinceStartup;
    }
    yield return null;
}
ctx.Result = new
{
    nudged = nudged.Count,
    // Overviews whose revision had moved on from the nudge by the end.
    moved = nudged.Count(kv => AuthoritativeItemRegistry.TryGet(kv.Key, out var r) && r.Revision > kv.Value)
};
