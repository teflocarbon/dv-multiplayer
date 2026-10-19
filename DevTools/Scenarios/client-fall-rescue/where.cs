// Either game: where its copy of the item is, and how far under this game's ground. Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) return new { present = false };
Vector3 abs = Dev.Abs(item.transform.position);
float? depth = NetworkedItemManager.BelowTerrain(item.transform.position, out float surface);
return new
{
    present = true,
    pos = new[] { abs.x, abs.y, abs.z },
    depth = depth ?? -999f,
    hasTerrain = depth != null,
    state = item.GetItemState().ToString()
};
