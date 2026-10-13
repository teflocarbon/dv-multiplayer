// Client, after rejoining: exactly one of each owned item, none left lying around.
// Args: names (comma list of the item names owned before leaving).
byte me = NetworkLifecycle.Instance.Client.PlayerId;
var mine = Dev.All<NetworkedItem>().Where(n => n.NetId != 0 && n.PersistentOwnerPlayerId == me).ToList();
var failures = new List<string>();
foreach (var g in mine.GroupBy(n => n.name.Replace("(Clone)", "")))
{
    if (g.Count() > 1) failures.Add($"{g.Key} x{g.Count()} ({string.Join(", ", g.Select(n => n.NetId + "=" + n.GetItemState()))})");
    foreach (var n in g.Where(n => n.GetItemState() != ItemState.InInventory && n.GetItemState() != ItemState.InHand))
        failures.Add($"{g.Key} {n.NetId} is {n.GetItemState()}, expected in inventory");
}
// Unbound local copies (NetId 0) sitting in inventory slots are duplicates the player would see.
var inventory = DV.InventorySystem.Inventory.Instance;
for (int slot = 0; slot < inventory.Capacity; slot++)
{
    var go = inventory.PeekItemAtSlot(slot, false);
    var n = go != null ? go.GetComponent<NetworkedItem>() : null;
    if (go != null && (n == null || n.NetId == 0))
        failures.Add($"unbound local {go.name.Replace("(Clone)", "")} in slot {slot}");
}
var expected = ctx.Args["names"].Split(',').Where(s => s.Length > 0).ToList();
foreach (var name in expected.Distinct())
    if (!mine.Any(n => n.name.Replace("(Clone)", "") == name)) failures.Add($"{name} not returned");
// Each item back in the slot it had. Reported, not failed: the host doesn't track where ordinary
// items sit, so a rearranged inventory comes back in the starting layout (known limitation).
var moved = new List<string>();
foreach (var entry in (ctx.Args.TryGetValue("slots", out var s) ? s : "").Split(',').Where(e => e.Contains("@")))
{
    string name = entry.Substring(0, entry.LastIndexOf('@'));
    int slot = int.Parse(entry.Substring(entry.LastIndexOf('@') + 1));
    var item = mine.FirstOrDefault(n => n.name.Replace("(Clone)", "") == name);
    int now = item != null ? inventory.IndexOf(item.gameObject) : -1;
    if (item != null && now != slot) moved.Add($"{name} {slot}->{now}");
}
return new { pass = failures.Count == 0, owned = mine.Count, failures, moved };
