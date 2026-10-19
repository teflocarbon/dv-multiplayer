// Client: put an item in the inventory, open a container it holds, deposit the item, withdraw it,
// then close and reopen the container. Args: box (container NetId), item (NetId to store).
// Needs: -u "using CUI = Multiplayer.Client.Items.ColdContainerUiIntegration;"
var inv = DV.InventorySystem.Inventory.Instance;
var log = new List<string>();
NetworkedItem Wait(ushort id) { NetworkedItem n; return NetworkedItem.TryGet(id, out n) ? n : null; }
ushort boxId = ushort.Parse(ctx.Args["box"]), itemId = ushort.Parse(ctx.Args["item"]);
NetworkedItem box = null, item = null;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 20f && ((box = Wait(boxId)) == null || (item = Wait(itemId)) == null);) yield return null; // a just-joined client can take a while
if (box == null || item == null) throw new Exception("box or item never arrived");
string prefab = item.Item.InventorySpecs.ItemPrefabName;
// A stateful item must come back with its state (run.sh drains a battery on the host, which owns it).
float? powerBefore = item.GetComponentInChildren<Battery>(true)?.CurrentPower;

var open = inv.ItemContainerRegistry.ActiveContainer; // leftovers from an earlier run
if (open != null) { open.ToggleContainerAccess(); for (int i = 0; i < 10; i++) yield return null; }
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(itemId);
inv.AddItemToInventory(item.gameObject);
for (int i = 0; i < 60; i++) yield return null;
if (inv.IndexOf(item.gameObject) < 0 || item.GetItemState() != ItemState.InInventory) throw new Exception("item did not go into the inventory: " + item.GetItemState());
yield return LocalPlayer.GoTo(box);
yield return Hands.Pickup(boxId);
var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);

IEnumerator Open()
{
    c.ToggleContainerAccess();
    float t0 = Time.realtimeSinceStartup;
    while (!CUI.Active && Time.realtimeSinceStartup - t0 < 10f) yield return null;
    if (!CUI.Active) throw new Exception("container view never arrived");
}
IEnumerator Mutate(Func<bool> request, string what)
{
    uint seq = CUI.MutationSequence;
    if (!request()) throw new Exception(what + " was not intercepted");
    float t0 = Time.realtimeSinceStartup;
    while (CUI.MutationSequence == seq && Time.realtimeSinceStartup - t0 < 10f) yield return null;
    var r = CUI.LastMutation;
    if (CUI.MutationSequence == seq || r == null) throw new Exception(what + ": no result");
    if (!r.Accepted) throw new Exception(what + " rejected: " + r.RejectionReason);
    for (int i = 0; i < 60; i++) yield return null; // the refreshed view
}

yield return Open();
log.Add($"opened: {CUI.ActiveRowCount}/{CUI.ActiveCapacity}");
int before = CUI.ActiveRowCount;
yield return Mutate(() => !c.AddItem(item.gameObject, -1) && true, "deposit");
bool goneFromInventory = item == null || inv.IndexOf(item.gameObject) < 0;
log.Add($"deposited: rows {CUI.ActiveRowCount}, gone from inventory {goneFromInventory}");
if (CUI.ActiveRowCount != before + 1 || !goneFromInventory) throw new Exception("deposit not reflected: " + string.Join("; ", log));
int slot = CUI.ActiveSlots.Last();

// close and reopen: the contents come from the host
c.ToggleContainerAccess();
for (int i = 0; i < 30; i++) yield return null;
yield return Open();
log.Add($"reopened: {CUI.ActiveRowCount} rows");
if (CUI.ActiveRowCount != before + 1) throw new Exception("contents lost on reopen: " + string.Join("; ", log));

yield return Mutate(() => !c.RemoveItem(slot, true, false) && true, "withdraw");
ushort materialized = CUI.LastMutation.MaterializedItemNetId;
NetworkedItem back = null;
float w0 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - w0 < 10f && ((back = Wait(materialized)) == null || inv.IndexOf(back.gameObject) < 0)) yield return null;
if (back == null || inv.IndexOf(back.gameObject) < 0) throw new Exception($"withdrawn item {materialized} not in the inventory: " + string.Join("; ", log));
log.Add($"withdrew: {back.NetId} {back.Item.InventorySpecs.ItemPrefabName} in slot {inv.IndexOf(back.gameObject)}, rows {CUI.ActiveRowCount}");
float? powerAfter = back.GetComponentInChildren<Battery>(true)?.CurrentPower;
if (powerBefore != null) log.Add($"battery {powerBefore:F1}% -> {powerAfter:F1}%");
if (powerBefore != null && (powerAfter == null || Mathf.Abs(powerAfter.Value - powerBefore.Value) > 1f)) throw new Exception("item state lost: " + string.Join("; ", log));
if (back.Item.InventorySpecs.ItemPrefabName != prefab || CUI.ActiveRowCount != before) throw new Exception("withdraw mismatch: " + string.Join("; ", log));
c.ToggleContainerAccess();
ctx.Result = new { box = boxId, withdrawn = back.NetId, log };
