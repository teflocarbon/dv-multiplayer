// Host: once the mug lies in the cab under the lease its drop opened, throw it out of the side of
// the loco with a host transition naming the actor, as the job re-roll throws a binned overview back
// out (Server/Jobs/JobReroll.cs), but without that code's workarounds: no RegisterHostWorldItem
// after it, no velocity set on the new lease. Then watch the host's copy fly.
// Args: item, loco, actor (host|client).
ushort id = ushort.Parse(ctx.Args["item"]);
var server = Multiplayer.Server.Session.NetworkServer.Instance;
ServerPlayer actor = ctx.Args["actor"] == "host" ? server.LocalPlayer
    : server.ServerPlayers.First(p => p != server.LocalPlayer);
var spatial = NetworkedItemManager.Instance.Spatial;
NetworkedItem item = null;
AuthoritativeItemRegistry.Record record = null;
bool leased = false, held = false;
float t0 = Time.realtimeSinceStartup, inCabAt = -1f;
while (Time.realtimeSinceStartup - t0 < 60f)
{
    yield return null;
    if (item == null) NetworkedItem.TryGet(id, out item);
    if (item == null || !AuthoritativeItemRegistry.TryGet(id, out record)) continue;
    // The mug is spawned at the actor's feet in the cab and may already lie there, recorded in it:
    // only a cab placement after the actor has picked it up is the drop.
    if (record.Placement == ItemPlacementKind.PlayerHand) held = true;
    if (!held) continue;
    if (record.Placement != ItemPlacementKind.TrainInterior)
    {
        if (inCabAt >= 0f) break; // it left the cab on its own
        continue;
    }
    if (inCabAt < 0f) inCabAt = Time.realtimeSinceStartup;
    // The drop's lease begins a frame or so after its transition: thrown before, the throw's
    // lease would be the only one, superseding nothing.
    leased = spatial.TryGetActiveLeaseIdentity(id, out _, out _, out byte simulator, out _) &&
             simulator == actor.PlayerId;
    if (leased || Time.realtimeSinceStartup - inCabAt > 2f) break;
}
if (!held)
    throw new Exception("the mug was never recorded in the " + ctx.Args["actor"] + "'s hand (" +
        (record?.Placement.ToString() ?? "no record") + ")");
if (record.Placement != ItemPlacementKind.TrainInterior)
    throw new Exception(inCabAt >= 0f
        ? "the mug left the cab on its own before the host threw it (" + record.Placement + ")"
        : "the mug was never recorded in the cab after the " + ctx.Args["actor"] + " dropped it (" + record.Placement + ")");
float droppedFor = Time.realtimeSinceStartup - inCabAt;

var loco = Trains.Find(ctx.Args["loco"]);
Vector3 side = loco.transform.right;
Vector3 start = loco.transform.position + side * 2.5f + Vector3.up * 3f;
Vector3 velocity = side * 2.5f + Vector3.up * 3.5f;
var snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
snapshot.ItemState = ItemState.Thrown;
snapshot.PlayerId = 0;
snapshot.ItemPosition = start - WorldMover.currentMove;
// GrabHandlerItem.Throw applies direction * mass * 140 as a force for one physics step.
snapshot.ThrowDirection = velocity / (140f * Time.fixedDeltaTime);
snapshot.WorldParentKind = ItemWorldParentKind.World;
snapshot.WorldParentNetId = 0;
snapshot.WorldParentKey = string.Empty;
snapshot.InventoryClaimPlayerId = 0;
snapshot.InventoryClaimSlot = -1;
snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
if (!AuthoritativeItemRegistry.TryApplyTransition(item, snapshot, actor, ItemTransitionReason.Unknown, true,
        out string rejection, cause: ItemChangeLog.Move(Multiplayer.Core.Items.ItemMoveReason.Thrown, actor, "dvmp server-throw")))
    throw new Exception("the throw was refused: " + rejection);
var manager = ServerItemManager.Instance;
// In its world cell the moment the transition is committed, before anything applies it.
bool indexedOnCommit = manager.HostCellByItem.ContainsKey(item) && !manager.HostNonSpatialItems.Contains(item);
AuthoritativeItemRegistry.TryGet(id, out record);
AuthoritativeItemRegistry.WriteToSnapshot(record, snapshot, ItemTransitionReason.Unknown);
snapshot.ItemState = ItemState.Thrown;
item.ApplyServerCanonicalSnapshot(snapshot);
server.SendItemUpdatePacket(snapshot);

Vector3 startAbs = start - WorldMover.currentMove;
float travel = 0f, rise = 0f;
float t1 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - t1 < 4f && item != null)
{
    yield return null;
    Vector3 at = Dev.Abs(item.transform.position) - startAbs;
    rise = Mathf.Max(rise, at.y);
    travel = Mathf.Max(travel, new Vector3(at.x, 0f, at.z).magnitude);
}
ctx.Result = new
{
    item = id,
    droppedFor,
    leaseLiveAtThrow = leased,
    indexedOnCommit,
    travel,
    rise,
    start = new[] { startAbs.x, startAbs.y, startAbs.z }
};
