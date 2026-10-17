// Client: turn the knob of a lantern lying in the world, as a VR hand does: grab the knob (DV's
// own grab event, which opens the interaction), set it, let go. Args: item, wick.
ushort id = ushort.Parse(ctx.Args["item"]);
float wick = float.Parse(ctx.Args["wick"], System.Globalization.CultureInfo.InvariantCulture);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
var lantern = item?.GetComponent<Lantern>() ?? throw new Exception("item " + id + " is not a lantern");
yield return new WaitForSecondsRealtime(2f); // its projection has settled
var knob = lantern.knob ?? throw new Exception("lantern " + id + " has no knob");
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
typeof(DV.CabControls.ControlImplBase).GetMethod("FireGrabbed", flags).Invoke(knob, null);
for (int i = 0; i < 5; i++)
{
    knob.SetValue(Mathf.Lerp(lantern.wickSize, wick, 0.5f));
    yield return new WaitForSecondsRealtime(0.1f);
}
knob.SetValue(wick);
yield return new WaitForSecondsRealtime(0.2f);
typeof(DV.CabControls.ControlImplBase).GetMethod("FireUngrabbed", flags).Invoke(knob, null);
yield return new WaitForSecondsRealtime(3f);
ctx.Result = new { wick = lantern.wickSize, state = item.GetItemState().ToString() };
