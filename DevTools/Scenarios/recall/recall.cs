// Client: recall an essential item into its reserved slot (what clicking the silhouette does) and
// wait for it to arrive.
ushort id = ushort.Parse(ctx.Args["item"]);
int slot = int.Parse(ctx.Args["slot"]);
if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) throw new Exception("no item " + id);
NetworkedItemManager.Instance.RequestItemRecall(item, slot);
var inv = DV.InventorySystem.Inventory.Instance;
float t0 = Time.realtimeSinceStartup;
bool Back() => inv.PeekItemAtSlot(slot, false) == item.gameObject && item.GetItemState() == ItemState.InInventory;
while (Time.realtimeSinceStartup - t0 < 10f && !Back()) yield return null;
ctx.Result = new { slot = Back() ? slot : -1, state = item.GetItemState().ToString(), seconds = Time.realtimeSinceStartup - t0 };
