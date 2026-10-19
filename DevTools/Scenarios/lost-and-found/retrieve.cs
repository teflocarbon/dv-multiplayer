// Client: wait for the item in the Lost & Found list, retrieve it, check it lands in the inventory.
ushort id = ushort.Parse(ctx.Args["item"]);
var client = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Client;
LostItemData entry = null;
float t0 = Time.realtimeSinceStartup;
while (entry == null && Time.realtimeSinceStartup - t0 < 30f)
{
    client.RequestLostItems(1);
    for (int i = 0; i < 30; i++) yield return null;
    entry = NetworkedLostAndFoundManager.ClientItems.FirstOrDefault(e => e.NetId == id);
}
if (entry == null) throw new Exception("item " + id + " never appeared in the client's Lost & Found list");
Multiplayer.Shared.Protocol.Packets.Clientbound.ClientboundLostItemRetrieveResultPacket result = null;
Action<Multiplayer.Shared.Protocol.Packets.Clientbound.ClientboundLostItemRetrieveResultPacket> on = p => { if (p.LostHandle == entry.Handle) result = p; };
client.LostItemRetrieveCompleted += on;
try
{
    client.RequestLostItemRetrieval(77, entry.Handle, entry.NetId, entry.Revision);
    t0 = Time.realtimeSinceStartup;
    while (result == null && Time.realtimeSinceStartup - t0 < 15f) yield return null;
}
finally { client.LostItemRetrieveCompleted -= on; }
if (result == null) throw new Exception("no retrieval result");
if (!result.Accepted) throw new Exception("retrieval rejected: " + result.RejectionReason);
NetworkedItem item = null;
t0 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - t0 < 15f)
{
    if (NetworkedItem.TryGet(id, out item) && item != null && DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject) >= 0) break;
    item = null;
    yield return null;
}
if (item == null) throw new Exception("retrieved item " + id + " is not in the inventory");
ctx.Result = new { item = id, slot = DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject), listed = NetworkedLostAndFoundManager.ClientItems.Count(e => e.NetId == id) };
