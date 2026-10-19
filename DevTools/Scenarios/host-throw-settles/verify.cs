// Either game: where its copy of the item is. On the host also the server's record. Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem.TryGet(id, out NetworkedItem item);
if (item == null) return new { present = false };
Vector3 abs = Dev.Abs(item.transform.position);
bool host = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsHost();
var record = host && AuthoritativeItemRegistry.TryGet(id, out var r) ? r : null;
bool known = record != null;
return new
{
    present = true,
    pos = new[] { abs.x, abs.y, abs.z },
    state = item.GetItemState().ToString(),
    revision = item.AuthorityRevision,
    leased = NetworkedItemManager.Instance.HasActiveSpatialState(id),
    recordPlacement = known ? record.Placement.ToString() : "",
    recordRevision = known ? record.Revision : 0,
    recordPos = known ? new[] { record.Position.x, record.Position.y, record.Position.z } : null
};
