// Host: item totals that must stay stable across save/reload cycles.
var all = Dev.All<NetworkedItem>().Where(i => i.NetId != 0).ToList();
var inventory = DV.InventorySystem.Inventory.Instance;
int hostInventoryMisrecorded = 0;
for (int slot = 0; slot < inventory.Capacity; slot++)
{
    var go = inventory.PeekItemAtSlot(slot, false);
    var n = go != null ? go.GetComponent<NetworkedItem>() : null;
    if (n != null && AuthoritativeItemRegistry.TryGet(n.NetId, out var r) && r.Placement != ItemPlacementKind.PlayerInventory)
        hostInventoryMisrecorded++;
}
return new
{
    items = all.Count,
    jobOverviews = all.Count(i => i.name.StartsWith("JobOverview")),
    activeInSky = all.Count(i => i.gameObject.activeInHierarchy && Dev.Abs(i.transform.position).y > 1500),
    lostAndFound = all.Count(i => NetworkedLostAndFoundManager.Contains(i.NetId)),
    hostInventoryMisrecorded
};
