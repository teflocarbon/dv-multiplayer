// Host: spawn an essential item at the host's feet owned by a player who isn't here (a new
// identity), with the client's player number as its owner number: a restored record whose owner
// hasn't joined, after the numbers moved on. Reports how many of that prefab the client owns
// (OwnedItemsSummary, what its join and /give use) before and after.
var server = Multiplayer.Server.Session.NetworkServer.Instance;
var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
var essentials = DV.Globals.G.Items.items.Where(i => i != null && i.IsEssential && !string.IsNullOrEmpty(i.ItemPrefabName)).ToList();
Func<string, int> owned = prefab => OwnedItemsSummary.For(client).Count(o => string.Equals(o.Prefab, prefab, StringComparison.OrdinalIgnoreCase));
string wanted = ctx.Args.TryGetValue("prefab", out var p) && !string.IsNullOrEmpty(p) ? p : null;
var spec = wanted != null
    ? essentials.FirstOrDefault(i => string.Equals(i.ItemPrefabName, wanted, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception(wanted + " isn't an essential item")
    : essentials.FirstOrDefault(i => owned(i.ItemPrefabName) == 0) ?? essentials.First(i => i.ItemPrefabName == "CommsRadio");
int before = owned(spec.ItemPrefabName);

var go = UnityEngine.Object.Instantiate(spec.gameObject, PlayerManager.PlayerTransform.position + Vector3.up * 0.6f + Vector3.right * 1.5f, Quaternion.identity, WorldMover.OriginShiftParent);
go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
NetworkedItem item = null;
AuthoritativeItemRegistry.Record record = null;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 10f; )
{
    item = go.GetComponent<NetworkedItem>();
    if (item != null && item.NetId != 0 && AuthoritativeItemRegistry.TryGet(item.NetId, out record))
        break;
    yield return null;
}
if (record == null) throw new Exception("the spawned " + spec.ItemPrefabName + " has no server record");
record.PersistentOwnerIdentity = Guid.NewGuid();
record.PersistentOwnerPlayerId = client.PlayerId;
yield return null;

ctx.Result = new
{
    prefab = spec.ItemPrefabName,
    item = item.NetId,
    number = client.PlayerId,
    before,
    after = owned(spec.ItemPrefabName)
};
