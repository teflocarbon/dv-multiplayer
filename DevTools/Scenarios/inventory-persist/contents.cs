// Client: the inventory by prefab, with how many of each are bound to the host (NetId != 0).
var inv = DV.InventorySystem.Inventory.Instance;
var rows = new List<string>();
for (int s = 0; s < inv.Capacity; s++)
{
    var go = inv.PeekItemAtSlot(s, false);
    if (go == null) continue;
    var n = go.GetComponent<NetworkedItem>();
    rows.Add(go.name.Replace("(Clone)", "") + (n != null && n.NetId != 0 ? "" : "*"));
}
return new { count = rows.Count(r => r.StartsWith(ctx.Args["prefab"])), bound = rows.Count(r => r == ctx.Args["prefab"]), all = string.Join(",", rows) };
