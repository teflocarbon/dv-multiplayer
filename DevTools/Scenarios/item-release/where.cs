// Where the item is relative to the car, on this peer. Args: item, car.
var car = Trains.Find(ctx.Args["car"]);
NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem item);
if (item == null) return new { present = false };
Vector3 local = car.transform.InverseTransformPoint(item.transform.position);
return new
{
    present = true,
    onCar = TrainCar.Resolve(item.transform) == car,
    local = new[] { local.x, local.y, local.z },
    state = item.GetItemState().ToString(),
    carNetId = (int)Multiplayer.Shared.Utils.DvExtensions.GetNetId(car)
};
