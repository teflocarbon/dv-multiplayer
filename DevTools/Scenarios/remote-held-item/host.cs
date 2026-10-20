// Host: the server's record of the item (placement and player), the client's player number, and
// whether the server's copy is lit.
ushort id = ushort.Parse(ctx.Args["item"]);
var server = Multiplayer.Server.Session.NetworkServer.Instance;
var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
AuthoritativeItemRegistry.TryGet(id, out var r);
NetworkedItem.TryGet(id, out NetworkedItem n);
var lantern = n == null ? null : n.GetComponentInChildren<Lantern>(true);
return new
{
    placement = r?.Placement.ToString() ?? "none",
    player = r?.PlacementPlayerId ?? 0,
    client = client.PlayerId,
    lit = lantern != null && lantern.Ignited
};
