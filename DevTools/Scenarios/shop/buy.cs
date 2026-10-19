// Either peer, at a shop: put ITEM in the basket, pay a little over the price from the wallet and
// buy (or with cancel=1, pay and cancel). Reports money, stock and the new items with their NetIds.
var reg = UnityEngine.Object.FindObjectsOfType<NetworkedCashRegisterWithModules>().Where(r => r.IsShopRegister).OrderBy(r => Vector3.Distance(r.transform.position, PlayerManager.PlayerTransform.position)).First();
var mod = reg.CashRegister.registerModules.OfType<ScanItemCashRegisterModule>().First(m => m.name.StartsWith(ctx.Args["item"]));
var w = Resources.FindObjectsOfTypeAll<Wallet>().First(x => x.gameObject.scene.IsValid() && x.GetComponentInParent<Multiplayer.Shared.Players.NetworkedPlayer>() == null);
bool cancel = ctx.Args.TryGetValue("cancel", out var c) && c == "1";
double start = Inventory.Instance.PlayerMoney;
int stock0 = GlobalShopController.Instance.GetShopItemData(mod.sellingItemSpec).ItemsInStock;
var before = new HashSet<ItemBase>(UnityEngine.Object.FindObjectsOfType<ItemBase>());
mod.AddItemsToBuy();
double cost = reg.CashRegister.GetTotalCost();
reg.CashRegister.AddCash(w.TrySpend(cost + 7));
for (int i = 0; i < 60; i++) yield return null;
bool bought = false;
if (cancel) reg.CashRegister.Cancel(); else bought = reg.CashRegister.Buy();
for (int i = 0; i < 300; i++) yield return null;
var fresh = UnityEngine.Object.FindObjectsOfType<ItemBase>().Where(x => !before.Contains(x) && x.name.StartsWith(mod.sellingItemSpec.ItemPrefabName, StringComparison.OrdinalIgnoreCase)).ToList();
ctx.Result = new { distance = Math.Round(Vector3.Distance(reg.transform.position, PlayerManager.PlayerTransform.position)), prefab = mod.sellingItemSpec.ItemPrefabName, cost, bought, start, after = Inventory.Instance.PlayerMoney, stock0, stock1 = GlobalShopController.Instance.GetShopItemData(mod.sellingItemSpec).ItemsInStock,
  netIds = string.Join(",", fresh.Select(x => x.GetComponent<NetworkedItem>()?.NetId ?? 0)),
  banknotes = UnityEngine.Object.FindObjectsOfType<ItemBase>().Count(x => !before.Contains(x) && x.name.StartsWith("Banknotes")) };
