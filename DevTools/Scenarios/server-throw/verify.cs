// Either side: where this game has the mug, whether a lease is still open on it, which Unity object
// it is (a copy retired by interest and re-created is another one), and on the host its record.
// Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) return new { present = false };
Vector3 p = Dev.Abs(item.transform.position);
bool host = NetworkLifecycle.Instance.IsHost();
AuthoritativeItemRegistry.Record record = null;
if (host) AuthoritativeItemRegistry.TryGet(id, out record);
return new
{
    present = true,
    instance = item.gameObject.GetInstanceID(),
    pos = new[] { p.x, p.y, p.z },
    state = item.GetItemState().ToString(),
    leased = NetworkedItemManager.Instance.HasActiveSpatialState(id),
    revision = item.AuthorityRevision,
    recordPlacement = record?.Placement.ToString()
};
