// Host: money, stock of PREFAB, whether item NETID exists here and who owns it.
var d = GlobalShopController.Instance.GetShopItemData(ctx.Args["prefab"]);
ushort id = ushort.Parse(ctx.Args["netId"]);
bool seen = id != 0 && UnityEngine.Object.FindObjectsOfType<NetworkedItem>().Any(n => n.NetId == id);
Multiplayer.Components.Networking.World.AuthoritativeItemRegistry.TryGet(id, out var rec);
return new { money = Inventory.Instance.PlayerMoney, stock = d.ItemsInStock, seen, owner = (int)(rec?.PersistentOwnerPlayerId ?? 0), client = (int)UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.Player.NetworkedPlayer>().First().PlayerId };
