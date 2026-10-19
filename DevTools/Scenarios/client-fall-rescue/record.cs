// Host: the server's record of the item and its last move. Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
if (!AuthoritativeItemRegistry.TryGet(id, out var r)) return new { present = false };
return new
{
    present = true,
    placement = r.Placement.ToString(),
    y = r.Position.y,
    moveReason = r.LastMove.Reason.ToString(),
    moveDetail = r.LastMove.Detail
};
