// Client: pick up a job overview with the real grabber and see whether it stays in the hand
// (a pickup the server refuses jumps back to the table).
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (float t0 = Time.realtimeSinceStartup; (!NetworkedItem.TryGet(id, out item) || item == null) && Time.realtimeSinceStartup - t0 < 10f;) yield return null;
if (item == null) throw new Exception("overview " + id + " isn't in the client's game");
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
string error = null;
yield return Steps.Try(Hands.Pickup(id), e => error = e.Message);
bool held = Hands.Held != null && Hands.Held.NetId == id;
yield return new WaitForSecondsRealtime(3f);
ctx.Result = new
{
    held,
    kept = Hands.Held != null && Hands.Held.NetId == id && item.GetItemState() == ItemState.InHand,
    error
};
