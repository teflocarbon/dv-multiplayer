// Either peer: pick up the payment banknotes and put them in the inventory (which banks them).
// Args: item (NetId).
ushort id = ushort.Parse(ctx.Args["item"]);
if (Hands.Held != null && Hands.Held.NetId != id) yield return Hands.Drop();
if (Hands.Held == null) yield return Hands.Pickup(id);
var n = Hands.Held;
DV.InventorySystem.Inventory.Instance.AddItemToInventory(n.gameObject);
for (int i = 0; i < 120; i++) yield return null;
Hands.ReleaseControl();
ctx.Result = new { wallet = DV.InventorySystem.Inventory.Instance.PlayerMoney };
