// Where the item is relative to the loco, on this peer.
var loco = Trains.Find(ctx.Args["loco"]);
NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem item);
if (item == null) return new { present = false };
Vector3 local = loco.transform.InverseTransformPoint(item.transform.position);
object record = null;
if (Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsHost() &&
    AuthoritativeItemRegistry.TryGet(item.NetId, out var r))
    record = new { placement = r.Placement.ToString(), parentKind = r.WorldParentKind.ToString(), parentNetId = r.WorldParentNetId };
return new
{
    present = true,
    inLoco = TrainCar.Resolve(item.transform) == loco,
    local = new[] { local.x, local.y, local.z },
    state = item.GetItemState().ToString(),
    record,
    locoNetId = Multiplayer.Shared.Utils.DvExtensions.GetNetId(loco)
};
