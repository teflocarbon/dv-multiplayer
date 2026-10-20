// Client, after rejoining: every owned item back once (as many of each kind as before leaving),
// in the inventory, and no unbound duplicates. Args: names (comma list of the owned item names
// before leaving, one entry per item), slots (comma list of netId@slot before leaving).
byte me = Multiplayer.Client.Session.NetworkClient.Instance.PlayerId;
var mine = Dev.All<NetworkedItem>().Where(n => n.NetId != 0 && n.PersistentOwnerPlayerId == me).ToList();
string Name(NetworkedItem n) => n.name.Replace("(Clone)", "");
var failures = new List<string>();
// Items that were in the inventory before leaving (an owned item left lying in the world stays there).
var wasInInventory = new HashSet<ushort>((ctx.Args.TryGetValue("slots", out var sl) ? sl : "").Split(',').Where(e => e.Contains("@")).Select(e => ushort.Parse(e.Substring(0, e.IndexOf('@')))));
foreach (var n in mine.Where(n => wasInInventory.Contains(n.NetId) && n.GetItemState() != ItemState.InInventory && n.GetItemState() != ItemState.InHand))
    failures.Add($"{Name(n)} {n.NetId} is {n.GetItemState()}, expected in inventory");
var expected = ctx.Args["names"].Split(',').Where(s => s.Length > 0).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
var actual = mine.Where(n => n.GetItemState() == ItemState.InInventory).GroupBy(Name).ToDictionary(g => g.Key, g => g.ToList());
foreach (var kv in expected)
{
    int have = actual.TryGetValue(kv.Key, out var items) ? items.Count : 0;
    if (have < kv.Value) failures.Add($"{kv.Key}: {have} of {kv.Value} returned (missing: {string.Join(", ", wasInInventory.Where(id => !mine.Any(n => n.NetId == id && Name(n) == kv.Key) && !actual.Values.SelectMany(v => v).Any(n => n.NetId == id)).Select(id => id + (NetworkedItem.TryGet(id, out NetworkedItem m) && m != null ? "=" + m.GetItemState() + "/o" + m.PersistentOwnerPlayerId : "=gone")))})");
}
foreach (var kv in actual)
{
    int want = expected.TryGetValue(kv.Key, out int w) ? w : 0;
    if (kv.Value.Count > want) failures.Add($"{kv.Key} x{kv.Value.Count}, expected {want} ({string.Join(", ", kv.Value.Select(n => n.NetId))})");
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
// Each item back in the slot it had. Reported, not failed: the host doesn't track where ordinary
// items sit, so a rearranged inventory comes back in the starting layout (known limitation).
var moved = new List<string>();
foreach (var entry in (ctx.Args.TryGetValue("slots", out var s) ? s : "").Split(',').Where(e => e.Contains("@")))
{
    ushort id = ushort.Parse(entry.Substring(0, entry.IndexOf('@')));
    int slot = int.Parse(entry.Substring(entry.IndexOf('@') + 1));
    var item = mine.FirstOrDefault(n => n.NetId == id);
    int now = item != null ? inventory.IndexOf(item.gameObject) : -1;
    if (item != null && now != slot) moved.Add($"{Name(item)} {slot}->{now}");
}
return new { pass = failures.Count == 0, owned = mine.Count, failures, moved };
