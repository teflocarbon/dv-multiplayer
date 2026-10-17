// Client: light the lantern it holds, the way a lighter does: wick up, then ignite. An item with
// no flame isn't changed (lit = true, so the step still runs).
NetworkedItem.TryGet(ushort.Parse(ctx.Args["item"]), out NetworkedItem n);
var lantern = n.GetComponentInChildren<Lantern>(true);
if (lantern != null)
{
    lantern.UpdateWickRelatedLogic(1f);
    yield return new WaitForSecondsRealtime(0.5f);
    lantern.Ignite(1f);
}
yield return new WaitForSecondsRealtime(1f);
ctx.Result = new { lit = lantern == null || lantern.Ignited };
