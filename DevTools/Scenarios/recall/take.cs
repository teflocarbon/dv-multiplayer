// Host: pick up the given item (another player's dropped essential).
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
for (int i = 0; i < 60; i++) yield return null;
Hands.ReleaseControl();
ctx.Result = new { held = Hands.Held?.NetId ?? 0 };
