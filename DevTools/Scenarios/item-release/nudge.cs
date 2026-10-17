// Client: change the wick of a lantern lying in the world, without picking it up. Only its holder
// may change an item's object state, so nothing should reach the server. Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
var lantern = item?.GetComponent<Lantern>() ?? throw new Exception("item " + id + " is not a lantern");
yield return new WaitForSecondsRealtime(2f); // its projection has settled
for (int i = 0; i < 10; i++)
{
    lantern.UpdateWickRelatedLogic(i % 2 == 0 ? 0.2f : 0.8f);
    yield return new WaitForSecondsRealtime(0.1f);
}
yield return new WaitForSecondsRealtime(2f);
ctx.Result = new { state = item.GetItemState().ToString() };
