// Host: its inventory by prefab (sorted, so two snapshots compare), the prefabs held more than
// once, and its absolute position.
var inv = DV.InventorySystem.Inventory.Instance;
var names = new List<string>();
for (int s = 0; s < inv.Capacity; s++)
{
    var go = inv.PeekItemAtSlot(s, false);
    if (go != null) names.Add(go.name.Replace("(Clone)", ""));
}
names.Sort(StringComparer.Ordinal);
var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key + " x" + g.Count());
var abs = Dev.PlayerAbs.Value;
return new
{
    inventory = string.Join(",", names),
    duplicates = string.Join(",", dupes),
    position = new { x = abs.x, z = abs.z },
};
