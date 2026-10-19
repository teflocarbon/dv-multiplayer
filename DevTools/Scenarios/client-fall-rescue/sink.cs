// Host: put the host's copy of the item 4 m under the client's ground, as when it falls through
// the terrain (#37), and send the clients that pose as convergence would (without waiting for its
// turn). Args: item, surface (absolute height of the ground there, from the client).
ushort id = ushort.Parse(ctx.Args["item"]);
float surface = float.Parse(ctx.Args["surface"], System.Globalization.CultureInfo.InvariantCulture);
if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) throw new Exception("no item " + id);
Vector3 abs = Dev.Abs(item.transform.position);
Vector3 under = Dev.World(new Vector3(abs.x, surface - 4f, abs.z));
Rigidbody body = item.Item.ItemRigidbody;
if (body != null) { body.isKinematic = true; body.position = under; }
item.transform.position = under;
var server = Multiplayer.Shared.Session.NetworkLifecycle.Instance.Server;
int sent = 0;
foreach (var player in server.ServerPlayers)
{
    if (player == server.LocalPlayer || !player.KnownItems.ContainsKey(item)) continue;
    if (!ServerItemManager.TryCreateConvergenceSnapshot(item, out var snapshot)) continue;
    server.SendItemUpdatePacket(snapshot, sendToPlayer: player);
    sent++;
}
return new { sent, hostHasTerrain = NetworkedItemManager.BelowTerrain(item.transform.position, out _) != null };
