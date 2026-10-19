// Client: where its item is (in its hand, its inventory, or the item's own state), and whether
// it's lit.
ushort id = ushort.Parse(ctx.Args["item"]);
if (!NetworkedItem.TryGet(id, out NetworkedItem n) || n == null) return new { state = "gone", lit = false };
var lantern = n.GetComponentInChildren<Lantern>(true);
string state = Hands.Held == n ? "InHand"
    : DV.InventorySystem.Inventory.Instance.IndexOf(n.gameObject) >= 0 ? "InInventory"
    : n.GetItemState().ToString();
return new { state, lit = lantern != null && lantern.Ignited };
