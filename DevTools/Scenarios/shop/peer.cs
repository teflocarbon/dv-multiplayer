// The other peer's view: money, stock of PREFAB, and whether item NETID exists here.
var d = GlobalShopController.Instance.GetShopItemData(ctx.Args["prefab"]);
ushort id = ushort.Parse(ctx.Args["netId"]);
bool seen = id != 0 && UnityEngine.Object.FindObjectsOfType<NetworkedItem>().Any(n => n.NetId == id);
return new { money = Inventory.Instance.PlayerMoney, stock = d.ItemsInStock, seen };
