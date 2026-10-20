// Host: make the item a personal one (as bought items are), pick it up so it becomes the host's, and put it down again. Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem.TryGet(id, out NetworkedItem n);
n.Item.InventorySpecs.BelongsToPlayer = true; // as a bought item is
yield return Hands.Pickup(id);
for (int i = 0; i < 30; i++) yield return null;
yield return Hands.Drop();
for (int i = 0; i < 60; i++) yield return null;
AuthoritativeItemRegistry.TryGet(id, out var r);
ctx.Result = new { owner = (int)(r?.PersistentOwnerPlayerId ?? 0), host = (int)Multiplayer.Client.Session.NetworkClient.Instance.PlayerId };
