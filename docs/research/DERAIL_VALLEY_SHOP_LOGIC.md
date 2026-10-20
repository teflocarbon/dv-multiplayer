# Derail Valley shop logic

> Status: read from the decompiled game (`Assembly-CSharp`, `DV.Inventory`, from the test install,
> 2026-10-21) and cross-checked against the game's own manual
> (`DerailValley_Data/StreamingAssets/manual/en.json`, pages "Shops", "Cash Register", "Trash Bin &
> Dumpster"). Not checked in game. Line numbers are left out because they change with each
> decompile; classes and methods are named instead.

How the unmodded game sells items: the shop and stock classes, a purchase from scan to delivery,
stock and restocking, prices, licence-gated items, what is saved, and every place that assumes one
player. The multiplayer design built on this is [`../design/shops.md`](../design/shops.md).

## In one paragraph

Every shop shares one global stock table (`GlobalShopController.shopItemsData`): one count per item
for the whole career, not per shop. A shop is a `Shop` with a `CashRegisterWithModules` and one
`ScanItemCashRegisterModule` per item it sells. The module is the item's shelf display, and
`ShopShelves` puts these displays on the shelves at random positions when the scene starts. The
player picks up the shop's scanner (an item) and shoots shelf items to add them to the register's
basket. They pay by using their wallet or a banknote on the register, then press Buy. The register
takes the price, prints change as a banknote, prints a receipt, and fires `ItemPurchased` for each
module. `GlobalShopController` then makes the items and puts them on the counter one every 0.5 s,
or leaves them in Lost and Found if the player is too far from the counter. Stock isn't saved. On
load it is counted from the bought items that still exist, and an item gives its stock back when it
is destroyed (binned, used up).

## Classes

| Class | What it is |
| --- | --- |
| `DV.Shops.GlobalShopController` | Singleton. `globalShopList` (every `Shop`, serialized order), `shopItemsData` (the stock table), the purchase queue and the spawning coroutine, `Restock`, `UpdateItemStocksOnGameLoad`. |
| `DV.Shops.ShopItemData` | One row of the stock table. Serialized: `item` (the `InventoryItemSpec` prefab), `shelfItem`, `amount` (the stock), `basePrice`, `isGlobal`, `careerOnly`, `posterPrefab`, `soldOnlyAt`. Runtime: `initialAmount`, `purchasedItems`, `allowedToHaveAmount`, `unavailableDueToGameMode`. `ItemsInStock = max(allowedToHaveAmount - purchasedItems, 0)`. |
| `Shop` | A shop building: `cashRegister`, `scanItemResourceModules` (what this shop sells, in a fixed serialized order), `itemSpawnTransform` (where bought items appear). `isGlobal` and `soldOnlyAt` are never read: a shop's range is simply its own modules. |
| `DV.CashRegister.CashRegisterBase` | Any register: `DepositedCash`, `IsProcessingTransaction`, `Buy`, `Cancel`, `AddCash`/`RemoveCash`/`SetCash`, the "return the money if the player walked off" check, the trigger that takes a banknote pushed into it, and the audio (`buyAudio`, `cancelAudio`, `notEnoughMoneyAudio`, `addCashAudio`, all heard 500 m away). |
| `DV.CashRegister.CashRegisterWithModules` | The register shops and service stations use: `registerModules`, Buy and Cancel buttons, three screens (`cashText`, `totalText`, `infoText`, via `CashRegisterWithModulesTextController`), a `PrinterController` for receipts. |
| `CashRegisterModule` | One line of a register: `Data.unitsToBuy`, `Data.pricePerUnit`, `resourceName`. `GetBoughtResource` is called on every module after a successful Buy. |
| `DV.Shops.ScanItemCashRegisterModule` | A shop's line for one item, and that item's shelf display: name and price tags (`itemNameText`, `itemPriceText`, which show "Sold out" or "Not available" instead of the price), `sellingItemSpec`, `AddItemsToBuy` (one more, if stock allows), and the `ItemPurchased(spec, shop, amount)` event. |
| `DV.Shops.ShopScanner` | On the scanner item. While grabbed it raycasts 4 m from the VR laser or the camera. Hovering a shelf item shows its name, "in basket / in stock" and price on the scanner's own screens, plays `hoverSound` and shows the item's description as a HUD notification. Using it calls `AddItemsToBuy` and plays `scanSound` or `errorSound` (heard 10 m away). The laser beam is on while it's grabbed and the big inventory is closed. |
| `DV.Shops.ShopShelves`, `ShelfPlacer`, `ShelfItem`, `ShopShelvesVerifiedSeeds` | Shelf layout. `ShopShelves.Start` finds the nearest `Shop`, picks a seed with `UnityEngine.Random.Range` from a list of verified seeds, and places each of that shop's modules (`ShelfItem`) at a random free spot on a random shelf. |
| `DV.Shops.ShoppingCartEntry` | A queued purchase: shop, spec, amount. |
| `DV.Shops.ShopRestocker` | On every shop item prefab. `restockOnItemDestroyed` is saved in the item's save data as `"Restock"`. When the item is destroyed outside a scene unload and the flag is set, it calls `GlobalShopController.Restock(prefab)`. |
| `DV.Shops.AShopRestockerLoadException` and its two subclasses | Per-item rules for how many may exist: `ShopRestockerWirelessMULoadException` (needs a licence), `ShopRestockerGarageLoadException` (garage keys). Below. |
| `DV.Shops.APurchaseTrigger`, `BoomboxLegalInfoTrigger` | Runs once when a bought item appears. The boombox's shows a one-time music-licensing popup. |
| `GenericThingCashRegisterModule` | A register line for "a thing" with a fixed price (the museum's loco restoration uses two). Not a shop. |
| `LocoResourceModule` | A service station's line (fuel, oil, sand, repairs). Not a shop. |
| `MoneyPrinter` | Prints a `Banknotes` item at an anchor with an amount. Every register has one: change and cancelled deposits come out as banknotes. |
| `Wallet` (`IMoney`) | The player's wallet. Its `Amount` is `Inventory.PlayerMoney`. `TrySpend` takes at most the amount asked. `ShouldDestroyOnUse` is false. |
| `Money` (`IMoney`, banknotes and coins) | Carries an amount, saved as `"amount"`. `TrySpend` gives up the whole amount. `ShouldDestroyOnUse` is true. |
| `DV.Interaction.MoneyUse` | On wallets and banknotes. Using one on a register calls `register.AddCash(money.TrySpend(register.GetRemainingCost()))`, so a wallet pays exactly what is still owed. A banknote gives everything it holds and is destroyed. |

## A purchase, end to end

1. **Scene start.** `GlobalShopController.Awake` → `InitializeShopData`: for each row,
   `initialAmount = amount`. Outside Career, `careerOnly` rows get `initialAmount = 0` and
   `unavailableDueToGameMode`, and every other row gets `basePrice = 0` (the manual: "In sandbox mode,
   shop items are available for free"). Then `allowedToHaveAmount = initialAmount`, and the item's
   `AShopRestockerLoadException` (if any) is initialised. It subscribes every shop's modules'
   `ItemPurchased` to `AddItemToInstantiationQueue`.
2. **Shelves.** `ShopShelves.Start` (one frame later) lays out the nearest shop's displays with a
   seed taken from `UnityEngine.Random`, so the layout changes every time the scene loads.
3. **Scan.** Holding the scanner, the player aims at a display (`Laser_Pointer_Target` or `Default`
   layer, 4 m) and uses it. `ScanItemCashRegisterModule.AddItemsToBuy` adds one unit if
   `ItemsInStock > 0` and `unitsToBuy < ItemsInStock`, so the basket never holds more than is in
   stock. A scan can't be undone line by line: Cancel empties the whole basket.
4. **The register shows** the total (`GetTotalCost`, the sum of `unitsToBuy × pricePerUnit`), the cash
   paid in, and a prompt: "select product", "insert wallet to pay", "ready to buy", "processing", or
   "goodbye".
5. **Pay.**
   - Wallet: `MoneyUse.HandleUse` on the register. `Wallet.TrySpend(remaining)` takes the remaining
     cost from `Inventory.PlayerMoney` (or all of it if short), and `AddCash` adds it. A wallet never
     overpays.
   - Banknote: pushed into the register's trigger while held (`CashRegisterBase.OnTriggerEnter`) or
     used on it. It spends its whole amount and is destroyed, so it can overpay.
   - Nothing is taken while nothing is owed (`GetRemainingCost() == 0`).
6. **Buy.** `CashRegisterWithModules.Buy` → `CashRegisterBase.Buy`. It refuses if the basket is
   empty, the cash is short, or `IsProcessingTransaction` (a previous purchase is still spawning).
   Otherwise:
   1. `RemoveCash(total)`.
   2. `ReturnLeftoverDepositedCash`: whatever is left is printed as a banknote at the register's
      `MoneyPrinter` anchor.
   3. The buy sound plays.
   4. A receipt is printed: `BookletCreator.CreateCashRegisterReceipt` instantiates a
      `ReceiptBooklet` at the printer anchor and renders up to four lines per page (name, units, unit
      price, line total, grand total).
   5. Every module's `GetBoughtResource` runs. A shop module resets `unitsToBuy` and fires
      `ItemPurchased(spec, shop, units)`.
7. **Queue.** `GlobalShopController.AddItemToInstantiationQueue` adds `units` to the row's
   `purchasedItems` (the stock drops now), queues a `ShoppingCartEntry`, and unlocks the item in the
   user's profile (`UnlockablesManager.UnlockItem`, which feeds the free-roam spawn menu).
8. **Spawn.** `Update` starts `InstantiatePurchasedItems` while the queue is non-empty:
   1. Every shop's register is set to `IsProcessingTransaction`, which blocks every shop's Buy and
      shows "processing", until the coroutine ends.
   2. Every queued item is instantiated from `Resources` with `BelongsToPlayer = true` and
      `ShopRestocker.restockOnItemDestroyed = true`.
   3. After one frame, every item is added to Lost and Found (`AddItemToLostAndFound`, transform
      untouched) and deactivated.
   4. Then, one item every 0.5 s: if `PlayerManager.PlayerTransform` is within the item's
      `RespawnOnDrop.maxDistance` of `shop.itemSpawnTransform`, it moves to World storage and appears
      on the counter, at rest, and its `APurchaseTrigger` runs. Otherwise it stays in Lost and Found
      (the player collects it at a tool shed). `RespawnOnDrop.Start` initialises on the frame after
      the instantiation, before the coroutine resumes. For an item that belongs to the player it sets
      the distance to `STANDARD_ESSENTIAL_RESPAWN_DISTANCE`, 200 m (prefabs can opt out with
      `shouldSetDefaultRespawnDistance`). With 0.5 s per item, only fast travel gets a player that far
      in time.
   5. If `PlayerManager.PlayerTransform` is null, the loop skips the item, which stays in Lost and
      Found.
9. **Leaving with cash in the register.** `ReturnMoneyToPlayerCheck` runs every 2 s while the
   register is enabled. With cash deposited and the player more than 20 m away, the cash goes back
   to the wallet (`Inventory.AddMoney`). Disabling the register (unloading, or culled with its
   scene) does the same, and `CashRegisterWithModules.OnDisable` also cancels the basket.

Cancel (`CashRegisterWithModules.Cancel`) plays the cancel sound if cash was in, prints the whole
deposit as a banknote, and empties every module.

## Stock and restocking

- **One table for every shop.** A row's stock is shared by every shop that has a module for it. The
  manual says the same: "Most items are limited in stock and can't be bought past a certain amount."
- **The amounts are scene data** (`ShopItemData.amount`, `basePrice` on the `GlobalShopController`
  in the world scene), not code. The v6→v7 save upgrader (`Upgrade_V6_to_V7_ShopSaving`) records
  the amounts of that time: shovel 2, lighter 3, steam manual 2, remote controller 1, caboose and
  garage keys 1, stopwatch 2, golden and expert shovels 1, boombox 1, each cassette 1, flashlight 2,
  lantern 3. The current table (gadgets, tools, containers) is longer. To read it in a game:
  `GlobalShopController.Instance.shopItemsData.Select(d => (d.item.ItemPrefabName, d.initialAmount, d.basePrice, d.careerOnly))`.
- **Stock is "how many may exist".** `ItemsInStock = allowedToHaveAmount - purchasedItems`.
  `purchasedItems` counts bought items still alive.
- **Restock on destroy.** `ShopRestocker.OnDestroy` calls `Restock(prefab)` (`purchasedItems--`,
  floored at 0) when `restockOnItemDestroyed` is set and the scene isn't unloading. Anything that
  destroys the GameObject counts: the bins and dumpsters (manual: "Items that are in limited supply
  in shops will be restocked if thrown in a trash bin or a dumpster"), use (manual: "Items that can be
  consumed restock automatically after use"), and any code path that destroys an item for another
  reason.
- **Not restocked:** a used garage key. `PadlockKey.KeyUsed` clears `restockOnItemDestroyed` on the
  key, so it no longer counts and never gives stock back.
- **Load.** Stock isn't saved. At the end of `StartGameData_FromSaveGame`'s non-blocking load (after
  `carsAndJobsLoadingFinished`), `GlobalShopController.UpdateItemStocksOnGameLoad` counts every item
  in every `StorageController` storage with `restockOnItemDestroyed`, adds each to its row's
  `purchasedItems` (stopping at `allowedToHaveAmount`), and then applies each row's load exception.
  The storages are world, Lost and Found, inventory, installed gadgets and item containers. An
  item that isn't in any of those lists isn't counted.
- **`Shop_item_amount_data`** (`SaveGameKeys.Shop_save_data`) is legacy. The v6→v7 upgrader wrote it
  and v10→v11 edits it, but nothing reads it now. The upgrader also set `"Restock": true` on the
  shop items in old saves, except one of each item the "expanded starting items" option had given.
  That is how a starting item tells itself apart from a bought one.

## Prices

`ScanItemCashRegisterModule.InitializeData` sets `pricePerUnit = basePrice` (∞ if the row is
missing). Prices are fixed: nothing in the shop code changes them with time, location or
reputation. Sandbox sets every price to 0. The total is `float` per line, summed as `double`;
`DepositedCash` and `Inventory.PlayerMoney` are `double`.

## Licence-gated and limited items

- **Wireless MU controller** (`ShopRestockerWirelessMULoadException`, field `license`, the MU
  general licence). At load, `ModifyAmount` sets `allowedToHaveAmount = 0` unless the licence is held.
  It also subscribes to `LicenseManager.LicenseAcquired`, which restores `initialAmount` and refreshes
  the shelves when the licence is bought mid-session. The manual: "Some items require owning a certain
  license, to be bought." Only buying is gated. Nothing here stops an installed controller working
  without the licence.
- **Garage keys** (`ShopRestockerGarageLoadException`, field `garageType`). At load
  `allowedToHaveAmount = 0` if that garage is already unlocked. Nothing updates it mid-session: a key
  bought and used in the same session leaves the row as it was until the next load (the used key no
  longer restocks either way).
- **Career only** (`careerOnly`): not sold outside Career (`initialAmount = 0`, "Not available").

## Delivery and Lost and Found

The shop has no delivery service of its own. Items appear at `Shop.itemSpawnTransform` (a missing
one is created 2 m above the shop and logged as an error). The only other destination is the Lost
and Found fallback in step 8 above. [`DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md`](DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md)
already names that case `PurchasedDeliveryFallback`.

## What is saved

| Data | Where | Notes |
| --- | --- | --- |
| Bought items | the normal storage lists (`Storage_World`, `Storage_LostAndFound`, `Storage_Inventory`, belt, installed gadgets, containers) | each with `"Restock"` in its save data |
| Stock | nowhere | recounted from items on load |
| Basket, deposited cash | nowhere | returned to the wallet on unload (`OnDisable`) |
| Shelf layout | nowhere | new random seed each load |
| Unlocked items | the user profile (`Unlocked_items`), not the save | `UnlockablesManager`, for the free-roam spawn menu |
| "Boombox info shown" | the user profile (`Boombox_info_displayed`) | per user, not per save |
| Money | `Inventory.PlayerMoney` in the save | one wallet |

## Single-player assumptions

Every one of these needs a server-side or per-player answer in multiplayer.

| Where | Assumption |
| --- | --- |
| `GlobalShopController.shopItemsData` | One stock table for the one player. |
| `InstantiatePurchasedItems` | Measures the delivery distance from `PlayerManager.PlayerTransform`. Skips delivery when it's null (a dedicated host). Blocks every shop's register while spawning. |
| `CashRegisterBase.ReturnMoneyToPlayerCheck` | Measures from `PlayerManager.PlayerTransform`. Pays back into `Inventory.Instance`. |
| `CashRegisterBase.OnDisable` | Pays the deposit back into the local `Inventory` whenever the register disables. |
| `CashRegisterBase.OnTriggerEnter`, `MoneyUse` | Money comes from the local wallet (`Inventory.PlayerMoney`) or a local banknote. |
| `ShopScanner` | Raycasts from `PlayerManager.PlayerCamera`. The description notification is the local HUD. |
| `ScanItemCashRegisterModule.UpdateTexts` | Shelf tags show "Sold out" from the one stock table. |
| `AddItemToInstantiationQueue` | Unlocks the item in the local user's profile. |
| `BoomboxLegalInfoTrigger` | Popup and flag in the local user's profile. |
| `GlobalShopController.InitializeShopData` | Game mode from `UserManager.CurrentUser.CurrentSession`. |
| `ShopRestockerWirelessMULoadException`, `ShopRestockerGarageLoadException` | Licences and garages from the one `LicenseManager`. |
| `ShopShelves.Start` | `UnityEngine.Random`: each game lays the shelves out differently. |
| `UpdateItemStocksOnGameLoad` | Stock is whatever the local storage lists hold at load. |
| `ShopRestocker.OnDestroy` | Destroying the GameObject means the item is gone. |
