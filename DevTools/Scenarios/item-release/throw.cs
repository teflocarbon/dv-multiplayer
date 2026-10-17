// Client: pick the lantern up, stand on the ground beside the car and throw it onto the deck.
// Hands.Throw does the real release (a Thrown transition; this game simulates the flight), then
// the body is put over the deck so it lands there whatever DV's throw force is. Args: item, car.
ushort id = ushort.Parse(ctx.Args["item"]);
var car = Trains.Find(ctx.Args["car"]);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
if (item == null) throw new Exception("item " + id + " never arrived");
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
Vector3 side = Dev.Abs(car.transform.position + car.transform.right * 3.5f);
yield return LocalPlayer.TeleportToGround(side.x, side.z);
yield return new WaitForSecondsRealtime(1f);
Vector3 deck = Trains.FloorPoint(car);
yield return Hands.Throw((deck + Vector3.up * 2f - PlayerManager.ActiveCamera.transform.position).normalized);
Rigidbody body = item.Item.ItemRigidbody;
body.position = deck + Vector3.up * 1.2f;
body.velocity = Vector3.down * 0.5f;
body.angularVelocity = Vector3.zero;
yield return new WaitForSecondsRealtime(5f);
ctx.Result = new { item = id };
