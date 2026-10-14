// Host, after the client quit. Args: held (NetId), inventory (comma list).
int held = int.Parse(ctx.Args["held"]);
var inventory = ctx.Args["inventory"].Split(',').Where(s => s.Length > 0).Select(int.Parse).ToList();
var failures = new List<string>();
foreach (int id in inventory.Append(held))
{
    var n = Dev.All<NetworkedItem>().FirstOrDefault(i => i != null && i.NetId == id);
    if (n == null) failures.Add($"{id} destroyed");
    else if (!AuthoritativeItemRegistry.TryGet((ushort)id, out _)) failures.Add($"{id} missing from authority registry");
}
var h = Dev.All<NetworkedItem>().FirstOrDefault(i => i != null && i.NetId == held);
if (h != null)
{
    AuthoritativeItemRegistry.TryGet((ushort)held, out var record);
    if (record == null || record.Placement != ItemPlacementKind.World) failures.Add($"held item record is {record?.Placement}, expected World");
    if (h.GetItemState() != ItemState.Dropped) failures.Add($"held item state {h.GetItemState()}, expected Dropped");
    if (h.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).All(c => !c.enabled)) failures.Add("held item has no enabled colliders");
}
return new { pass = failures.Count == 0, failures };
