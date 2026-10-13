// Client: pick the item up (making it this player's) and drop it at their feet.
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
if (item == null) throw new Exception("item " + id + " never arrived");
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
for (int i = 0; i < 30; i++) yield return null;
yield return Hands.Drop();
for (int i = 0; i < 120; i++) yield return null;
ctx.Result = new { item = id };
