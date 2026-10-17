// Client: pick the item up, let go, and take it again `delay` seconds later (0: the next frame, by
// force-hold, as a VR hand swap does). Reports the server revision seen while held before letting go.
// Args: item, delay.
ushort id = ushort.Parse(ctx.Args["item"]);
float delay = float.Parse(ctx.Args["delay"], System.Globalization.CultureInfo.InvariantCulture);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
if (item == null) throw new Exception("item " + id + " never arrived");
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
yield return new WaitForSecondsRealtime(1.5f); // the pickup is acknowledged
uint heldRevision = item.AuthorityRevision;
yield return Hands.Drop();
if (delay <= 0f)
{
    var handler = item.GetComponent<DV.Interaction.GrabHandlerItem>();
    Hands.Grabber.GetComponent<DV.Interaction.GrabberInteractionHandlerDV>().RequestForceHold(handler);
    yield return null;
}
else
{
    yield return new WaitForSecondsRealtime(delay);
    yield return Hands.Pickup(id);
}
yield return new WaitForSecondsRealtime(2.5f);
ctx.Result = new { heldRevision, held = Hands.Held != null && Hands.Held.NetId == id };
