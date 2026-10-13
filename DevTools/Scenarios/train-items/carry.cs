// The carrying player: fetch the given mug, board the loco's cab with it and drop or throw it.
// Args: loco, item, mode (drop|throw), moving (1 if the train is already moving).
var loco = Trains.Find(ctx.Args["loco"]);
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
if (item == null) throw new Exception("mug " + id + " never arrived");
if (Hands.Held != null) yield return Hands.Drop(); // leftovers from an earlier run
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
yield return Trains.Board(loco);
for (int i = 0; i < 20; i++) yield return null;
if (ctx.Args["mode"] == "throw")
    yield return Hands.Throw(loco.transform.forward * 0.6f - Vector3.up * 0.3f); // at the floor: a throw out of the open cab is physics, not sync
else
    yield return Hands.Drop();
for (int i = 0; i < 90; i++) yield return null;
ctx.Result = new { item = id };
