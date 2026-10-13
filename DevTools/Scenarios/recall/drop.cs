// Client: drop the essential item in the given inventory slot into the world (as dropping it from
// the inventory does), leaving its reserved silhouette behind.
int slot = int.Parse(ctx.Args["slot"]);
var inv = DV.InventorySystem.Inventory.Instance;
var go = inv.PeekItemAtSlot(slot, false) ?? throw new Exception("slot " + slot + " is empty");
var n = go.GetComponent<NetworkedItem>();
inv.DropItemFromHandsOrInventory(slot);
for (int i = 0; i < 90; i++) yield return null;
ctx.Result = new { item = n.NetId, name = go.name.Replace("(Clone)", ""), state = n.GetItemState().ToString(), reserved = inv.GetSlotReservedState(slot) };
