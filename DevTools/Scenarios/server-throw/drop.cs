// The actor: fetch the given mug, board the loco's cab with it and drop it there. Returns at once:
// the host throws it out while the drop's lease is still open. Args: loco, item.
var loco = Trains.Find(ctx.Args["loco"]);
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
if (item == null) throw new Exception("mug " + id + " never arrived");
if (Hands.Held != null) yield return Hands.Drop(); // leftovers from an earlier run
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
yield return Trains.Board(loco);
// No cab interior, no car to land in (see train-items/carry.cs).
float boarded = Time.realtimeSinceStartup;
while (loco.loadedInterior == null && Time.realtimeSinceStartup - boarded < 20f) yield return null;
if (loco.loadedInterior == null) throw new Exception("the cab interior never loaded in 20 s: not an item failure");
for (int i = 0; i < 20; i++) yield return null;
yield return Hands.Drop();
ctx.Result = new { item = id, interiorWait = Time.realtimeSinceStartup - boarded };
