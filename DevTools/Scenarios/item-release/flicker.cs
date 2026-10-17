// Host: once the item is let go, keep changing its lantern's wick on the server's copy for a few
// seconds, as a lantern's flame changes on its own. Each change is the server's object state and
// advances the item's revision while the item is in flight or lying in the world.
// Args: item, seconds.
ushort id = ushort.Parse(ctx.Args["item"]);
float seconds = float.Parse(ctx.Args["seconds"], System.Globalization.CultureInfo.InvariantCulture);
NetworkedItem.TryGet(id, out NetworkedItem item);
var lantern = item?.GetComponent<Lantern>() ?? throw new Exception("item " + id + " is not a lantern");
bool Held() => AuthoritativeItemRegistry.TryGet(id, out var r) &&
    r.Placement == Multiplayer.Networking.Data.Items.ItemPlacementKind.PlayerHand;
float deadline = Time.realtimeSinceStartup + 60f;
while (Time.realtimeSinceStartup < deadline && !Held()) yield return null;   // picked up
while (Time.realtimeSinceStartup < deadline && Held()) yield return null;    // and let go
if (Time.realtimeSinceStartup >= deadline) throw new Exception("item " + id + " was never picked up and let go");
uint from = item.AuthorityRevision;
int changes = 0;
for (float end = Time.realtimeSinceStartup + seconds; Time.realtimeSinceStartup < end; changes++)
{
    lantern.UpdateWickRelatedLogic(changes % 2 == 0 ? 0.3f : 0.7f);
    yield return new WaitForSecondsRealtime(0.1f);
}
ctx.Result = new { changes, revisions = item.AuthorityRevision - from };
