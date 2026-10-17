// Host: the server's record of the item. Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
if (!AuthoritativeItemRegistry.TryGet(id, out var r)) return new { present = false };
return new
{
    present = true,
    placement = r.Placement.ToString(),
    player = (int)r.PlacementPlayerId,
    revision = r.Revision,
    parentKind = r.WorldParentKind.ToString(),
    parentNetId = (int)r.WorldParentNetId
};
