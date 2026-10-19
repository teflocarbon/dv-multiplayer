// Host: the authority record and Lost & Found state of an item.
ushort id = ushort.Parse(ctx.Args["item"]);
bool known = AuthoritativeItemRegistry.TryGet(id, out var r);
return new {
    known,
    placement = known ? r.Placement.ToString() : "",
    owner = known ? (int)r.PersistentOwnerPlayerId : -1,
    placementPlayer = known ? (int)r.PlacementPlayerId : -1,
    revision = known ? r.Revision : 0,
    lost = NetworkedLostAndFoundManager.Contains(id),
    exists = NetworkedItem.TryGet(id, out NetworkedItem n) && n != null,
    clientId = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().PlayerId
};
