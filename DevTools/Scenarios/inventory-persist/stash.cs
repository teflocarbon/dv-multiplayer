// Client: pick the item up and put it in the inventory; report what the inventory holds.
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (float t0 = Time.realtimeSinceStartup; (!NetworkedItem.TryGet(id, out item) || item == null) && Time.realtimeSinceStartup - t0 < 20f;) yield return null;
if (item == null) throw new Exception("item " + id + " never arrived");
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
bool hand = ctx.Args.TryGetValue("hand", out var h) && h == "1";
if (!hand) DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
for (int i = 0; i < 90; i++) yield return null;
if (item.GetItemState() != (hand ? ItemState.InHand : ItemState.InInventory)) throw new Exception("not where it should be: " + item.GetItemState());
ctx.Result = new { item = id, slot = DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject) };
