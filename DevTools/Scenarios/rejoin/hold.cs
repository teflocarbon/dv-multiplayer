// Client: pick up the nearest loose item and report it plus everything this player owns.
byte me = NetworkLifecycle.Instance.Client.PlayerId;
if (Hands.Held != null) yield return Hands.Drop(); // leftovers from an earlier run
var item = Dev.All<NetworkedItem>()
    .Where(n => n.NetId != 0 && n.gameObject.activeInHierarchy && n.GetItemState() == ItemState.Dropped
                && n.GetComponent<DV.Interaction.GrabHandlerItem>() != null && TrainCar.Resolve(n.transform) == null)
    .OrderBy(n => Vector3.Distance(n.transform.position, PlayerManager.PlayerTransform.position)).First();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(item.NetId);
yield return new WaitForSeconds(2);
Hands.ReleaseControl();
var owned = Dev.All<NetworkedItem>().Where(n => n.NetId != 0 && n.GetItemState() == ItemState.InInventory && n.PersistentOwnerPlayerId == me).ToList();
ctx.Result = new
{
    held = (int)Hands.Held.NetId,
    heldName = Hands.Held.name,
    inventory = string.Join(",", owned.Select(n => n.NetId)),
    names = string.Join(",", owned.Select(n => n.name.Replace("(Clone)", "")).OrderBy(s => s)),
    slots = string.Join(",", owned.Select(n => n.name.Replace("(Clone)", "") + "@" + DV.InventorySystem.Inventory.Instance.IndexOf(n.gameObject)).OrderBy(s => s))
};
