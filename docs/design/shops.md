# Shops

> Status: designed 2026-10-21, in progress: PRs 0 and 1 are open (#280, #279), the rest isn't built. Vanilla behaviour is in
> [`../research/DERAIL_VALLEY_SHOP_LOGIC.md`](../research/DERAIL_VALLEY_SHOP_LOGIC.md); this doc
> refers to it rather than repeating it. What the mod does today is in [Today](#today), and in
> [`../world-sync.md`](../world-sync.md#shops) until this lands.

A shop is the start of an item's life: scan it, pay, buy it, and it's yours. This doc covers
everything up to the moment the item exists with its owner. From that moment the item belongs to the
owned-items design ([`owned-items.md`](owned-items.md), PR #275), and installing it on a train to
the gadget design ([`gadgets.md`](gadgets.md), PR #273).

The shop becomes one server transaction. The server owns each register's basket and cash, each
player's stock, and the delivery. Every game shows the same shelves, registers, screens and sounds.
Players predict their own scans, payments and purchases, so a player at 250 ms doesn't wait for the
round trip. A purchased item is made by the server and is owned by its buyer before anyone sees it.

## Implementation status

In progress (2026-10-21). See the [plan](#plan).

- PR 0, no restock on a retirement: open as #280. Passed `shop-restock` and `shop` on both host
  kinds.
- PR 1, the same shelves everywhere: open as #279. Passed `shop-layout` and `shop` on both host
  kinds. It sends only the shelf seed and the shop check; prices and availability move to PR 2,
  where the server first decides with them.
- PRs 2 to 6: not started.

## Decisions

Decided with the user on 2026-10-21:

- **Stock is per player.** Each player may own up to vanilla's amount of each item (one golden
  shovel, two shovels, one boombox, ...). The count is the items they own that came from a shop,
  wherever those items are. Vanilla's one count for the whole career would let one player who never
  comes back lock the only golden shovel away for good, because owned items never expire.
- **One basket per register, shared.** Everyone at a shop sees the same basket on the register, and
  anyone can scan into it, pay or press Buy. **Whoever scanned a line owns what it buys**, so
  friends can shop together and one of them can pay.
- **No refunds, as in vanilla.** A bought item can't be sold back. Unspent cash in a register
  always comes back: change, Cancel, walking away, disconnecting, the server stopping.
- **Delivery as in vanilla, per owner.** Bought items appear on the counter one every 0.5 s. When
  an item comes due, it goes on the counter if its owner is within 200 m of the counter. If the owner
  isn't (fast travel, disconnect, crash), it goes to their Lost and Found as
  `PurchasedDeliveryFallback`.

Decided here, following vanilla. Each could change without changing the design:

- **Cancel empties the whole basket**, everyone's lines, and prints all the cash as a banknote, as
  vanilla's one Cancel button does. See [Open questions](#open-questions).
- **Change and a cancelled deposit come out as a banknote** at the register, as in vanilla. The
  server makes the banknote, a synced item. Walking away returns the cash to the wallet, also as in
  vanilla.
- **A Buy only blocks its own register** while its items are delivered. Vanilla blocks every shop in
  the world, which with several players would stop shops miles away.

## Today

What the mod does, read from `beta` at `c6a53e7b` (2026-10-21). Not run in game except where the
`shop` scenario covers it. Paths are relative to `Multiplayer/`.

### How it works

- **Each game runs its own shop register.** A client scans into its own copy of the basket, and
  vanilla's Buy and Cancel run on that client. Only the money and the purchase go to the server
  (`Server/Stations/CashRegisterWithModulesPatch.cs:51-52`, `:94-95`, `:122-123`, `:144-145`:
  "each game's shop register is separate").
- **Payment escrow.** When a client pays into a shop register, `AddCash` sends a
  `ServerboundShopPaymentPacket` (`Client/Stations/CashRegisterBasePatch.cs:30-39`). Change and
  cancelled deposits send a negative one (`:48-66`). The server takes the money from the wallet into
  that player's escrow, `ServerPlayer.ShopCashInRegisters` (`Server/Session/ServerPlayer.cs:132-133`;
  `Server/Session/NetworkServer.Stations.cs:134-165`).
- **Purchase.** A client's `AddItemToInstantiationQueue` sends a `ServerboundShopPurchasePacket`
  (shop index, prefab, amount) instead of spawning, and lowers its own stock at once
  (`Client/World/GlobalShopControllerClientPatch.cs:17-33`). The server checks the escrow (±$0.50),
  the stock and a 50 m distance (`NetworkServer.Stations.cs:183-191`), and refunds the escrow if any
  check fails (`:193-205`). On success it updates the stock and unlocks the item (`:207-213`). Then
  `SpawnShopPurchase` (`:219-251`) puts the items on the counter, one every 0.5 s, owned by the buyer
  by number and GUID (`:237-245`).
- **Stock.** `SendShopStock` sends every row's `purchasedItems` (`NetworkServer.Stations.cs:253-267`):
  on join (`NetworkServer.cs:1062`), after every purchase and after every `Restock`
  (`Server/World/GlobalShopControllerPatch.cs:15-29`). A client applies it in
  `NetworkClient.Stations.cs:38-50`. `Restock` is a no-op on clients
  (`GlobalShopControllerClientPatch.cs:36-42`).
- **The host's own player** buys through vanilla: `AddItemToInstantiationQueue` only intercepts
  remote clients (`GlobalShopControllerClientPatch.cs:23`), and its register runs vanilla.
- **Registers are numbered on each game** by sorting positions (`Shared/Stations/NetworkedCashRegisterWithModules.cs:42-47`,
  `IsIdServerAuthoritative => false` at `:71`).

### Gaps and bugs

"Code" means read from the code and not seen in game. [To check in game](#to-check-in-game) lists how
to confirm each one.

| # | Gap or bug | Where | Effect | Fixed in |
| --- | --- | --- | --- | --- |
| 1 | Registers aren't shared. | `CashRegisterWithModulesPatch.cs:51-52`, `:94-95` | Two players at a shop each see only their own basket and cash. Nobody else sees or hears a scan, a payment, a purchase or the "goodbye" screen. Vanilla's register sounds carry 500 m. | [PR 2](#2-the-register-on-the-server) |
| 2 | Shelf layouts differ between games (confirmed: 38 of 244 displays, up to 5.6 m apart). | `ShopShelves.Start` uses `UnityEngine.Random`; nothing in the mod touches it | Each player sees the items in different places on the shelves. | [PR 1](#1-the-same-shelves-everywhere) |
| 3 | The host's own purchases are unowned, run vanilla, and skip the server. | `GlobalShopControllerClientPatch.cs:23` | Whoever picks the item up first becomes its owner. The host's player is the one player whose purchase isn't a request (the split's rule is that it should be). Vanilla delivery measures from the host player. | [PR 2](#2-the-register-on-the-server) |
| 4 | Escrow is per session. | `ServerPlayer.cs:132-133` | Cash a client paid in and didn't spend is lost when they disconnect (also in `world-sync.md`'s known issues). | [PR 2](#2-the-register-on-the-server) |
| 5 | A client's register never gives cash back when they walk away. | `CashRegisterBasePatch.cs:68-77` skips `CashRegisterBase.OnEnable` on clients, so `ReturnMoneyToPlayerCheck` never runs there; `:79-90` skips `OnDisable` | Cash stays in that client's register (and the escrow) until they buy, cancel, or disconnect and lose it (#4). | [PR 2](#2-the-register-on-the-server) |
| 6 | A rejected purchase looks like a sale. | the client's vanilla `Buy` has already run when the server rejects (`NetworkServer.Stations.cs:193-205`) | Buy sound, receipt, empty basket, and then no item. The refund goes quietly to the wallet. Nothing says why. | [PR 2](#2-the-register-on-the-server), [PR 4](#4-prediction) |
| 7 | Licence and garage limits don't apply on clients. | a client loads through `StartGameData_ServerSave`, which never calls `GlobalShopController.UpdateItemStocksOnGameLoad`, so `ShopRestockerWirelessMULoadException.ModifyAmount` and the garage rule never run there; the stock packet carries `purchasedItems` but not `allowedToHaveAmount` | Without the MU licence a client sees the wireless MU controller for sale and can scan, pay and buy it. The server then refuses (its own row allows 0) and refunds (#6). The same goes for a key to a garage that's already unlocked. | [PR 2](#2-the-register-on-the-server) |
| 8 | The server doesn't check that the shop sells the item. | `NetworkServer.Stations.cs:171-176` looks up the stock table, not `shop.scanItemResourceModules` | A modified client can buy any item at any shop. | [PR 2](#2-the-register-on-the-server) |
| 9 | Escrow isn't tied to a register. | `ShopCashInRegisters` is one number per player | Pay at one shop, buy at another. Harmless with one wallet, wrong once there are companies. | [PR 2](#2-the-register-on-the-server) |
| 10 | Receipts exist only for the buyer (code). | the client's vanilla `Buy` prints `ReceiptBooklet` locally (`BookletCreator_CashRegisterReceipt.Create`); it isn't `BelongsToPlayer`, so nothing adopts it | Other players never see a client's receipt. The host's receipts are ordinary host items, so everyone sees those. | [PR 3](#3-delivery-receipts-and-the-buyers-own-effects) |
| 11 | The buyer's own effects run on the server. | `SpawnShopPurchase` runs `APurchaseTrigger.OnPurchased` (`NetworkServer.Stations.cs:234-235`) and `UnlockItem` (`:210`) | When a client buys a boombox, the licensing popup shows on the host player's screen (and on a dedicated host, on nobody's). The unlock goes into the host's user profile, never the buyer's. | [PR 3](#3-delivery-receipts-and-the-buyers-own-effects) |
| 12 | No Lost and Found fallback. | `SpawnShopPurchase` always uses the counter; `LostItemReason.PurchasedDeliveryFallback` (`Multiplayer.Core/Items/LostAndFound.cs:13`) is never used | A buyer who fast-travels away leaves their items on the counter. Lost and Found picks them up later, unless someone is near. | [PR 3](#3-delivery-receipts-and-the-buyers-own-effects) |
| 13 | Putting a bought item in a container restocks the shop (confirmed: stock 11 -> 12). | a cold deposit destroys the item's GameObject on the server (`Server/Items/NetworkedColdContainerManager.cs:233`), and vanilla's `ShopRestocker.OnDestroy` restocks on any destroy outside a scene unload | Buy the only shovel, put it in a toolbox, and the shop has one again: unlimited shovels. Any other server-side destroy that isn't the item's end does the same. | [PR 0](#0-only-if-13-is-confirmed-no-restock-on-a-retirement), then [PR 6](#6-per-player-stock) |
| 14 | Stock comes back on every load (code). | `UpdateItemStocksOnGameLoad` runs at `carsAndJobsLoadingFinished` and counts `StorageController`'s lists. The mod's items are restored from `WorldItemsV1` on its own schedule (`Server/Items/WorldItemPersistenceManager.cs:52-58`), offline owners' possessions only when the owner connects, and cold-container contents never enter those lists | After a reload, whatever the count missed is for sale again. | [PR 6](#6-per-player-stock) |
| 15 | Other players' scanners are dead. | nothing in the mod handles `ShopScanner` | A remote player holding the scanner shows no laser and no screen text, and their scans make no sound. | [PR 5](#5-the-scanner) |
| 16 | Money is a `float`, sent unordered. | `world-sync.md` known issues | Large balances lose cents, so the price you see and the price you pay can differ by cents. Two updates out of order leave the older balance. | [PR 2](#2-the-register-on-the-server) |
| 17 | A used garage key keeps restocking on the server (code). | `PadlockKey.KeyUsed` clears `restockOnItemDestroyed` on the game that used the key, which is the client's copy | The server's copy still restocks if it's destroyed later. | [PR 6](#6-per-player-stock) |
| 18 | `world-sync.md` says shops are "Synced, escrowed". | `docs/world-sync.md` | True of money and items. The registers, shelves and scanner are not synced. | PR 2 updates it |

## The model

### Who owns what

The server owns all shop state. No game, the player host's included, decides anything about a shop
by itself.

| State | Owner | Saved | Notes |
| --- | --- | --- | --- |
| The catalogue: shops, what each sells, prices, availability | server (vanilla's scene data, read once) | no (scene data) | Sent at join, so a client never uses its own copy for anything the server decides. |
| Shelf layout | server picks one seed per `ShopShelves` per session | no, as vanilla | |
| Each register: basket lines (module, owner, units), deposited cash, phase, revision | server | no (cash is added to the saved money, see [Persistence](#persistence)) | One per shop. |
| Each player's remaining stock per item | server, derived | no (derived from owned items) | [Per-player stock](#per-player-stock). |
| Pending deliveries | server | no; a save during delivery delivers the rest first | |
| Money | server (one wallet today, one per company later) | yes, vanilla's `Money` | |
| The bought item | owned items, from birth | yes | [Owned items](#owned-items). |
| The receipt | server, an unowned world item | yes, as a world item | [Receipts](#receipts). |

### Where the code goes

- `Multiplayer.Core/Shops/`: the rules, with no Unity in them, unit-tested in
  `Multiplayer.Core.Tests/Shops/`. A basket (lines, totals, revision), scan, deposit and buy
  validation, the remaining-stock calculation, and the delivery choice.
- `Multiplayer/Server/Shops/`: the server's shops (`ServerShops`, one `ServerShopRegister` per
  shop), delivery, the handlers (`NetworkServer.Shops.cs`, out of `NetworkServer.Stations.cs`), and
  the save hook.
- `Multiplayer/Client/Shops/`: the shop view on every game, the player host's included. It applies
  server state to vanilla's register, modules, shelf tags and scanner, predicts this player's own
  requests, and sends them. It holds the patches that stop vanilla deciding anything.
- `Multiplayer/Shared/Shops/`: what both need, such as the shop and module indices and the catalogue
  checksum.
- Removed: the shop branches of `CashRegisterBasePatch`, `CashRegisterWithModulesPatch` and
  `NetworkedCashRegisterWithModules` (`IsShopRegister`), `GlobalShopControllerClientPatch`,
  `GlobalShopControllerPatch`, `ServerPlayer.ShopCashInRegisters`, and the three shop packets. Service
  station registers keep `NetworkedCashRegisterWithModules`, unchanged.

### Identifying things on the wire

- **Shop:** its index in `GlobalShopController.globalShopList` (serialized order, the same on every
  game).
- **Line:** the module's index in `Shop.scanItemResourceModules`.
- **Item:** its index in the catalogue the server sends at join (prefab names in `shopItemsData`
  order).
- **Check at join:** the catalogue carries a checksum of each shop's module prefab list. A client
  whose shops differ (a mod that adds shop items on one side only) is refused, the same way
  [track-graph-check.md](track-graph-check.md) refuses a client whose tracks are numbered
  differently.
- Registers are no longer found through `NetworkedCashRegisterWithModules`'s position-sorted ids.

## A purchase

### Scanning

The player aims the scanner at a shelf display and uses it.

1. **The holder's game** checks what vanilla checks (the display is this shop's, and the player has
   stock left counting what's in the baskets). If those pass, it predicts the scan: the line appears
   on the register, the scan sound plays, the scanner shows the new count. It sends
   `ServerboundShopScanPacket`. If its own view already says no, it plays the error sound and sends
   nothing.
2. **The server** checks:
   - the player holds a shop scanner (the server knows who holds each item);
   - they're within 6 m of the display;
   - the module belongs to that shop;
   - the item is available in this game mode;
   - the licence, for the MU controller: the company holds it;
   - the garage, for a garage key: it isn't unlocked;
   - the player has stock left: their remaining count minus what they already have in any basket.
3. **Accepted:** it adds one unit to that player's line for that module, bumps the revision and
   broadcasts the register state with the event `Scanned` (who, which module, which scanner).
   Every other game plays the scan sound at that scanner.
4. **Refused:** the requester gets the reason, and its prediction is undone (the line goes, and the
   reason shows on its own HUD). The register doesn't change. The server broadcasts the event
   `ScanRefused` with no state change, so every game plays the error sound at the scanner, as vanilla
   plays it there.

### Paying

Using the wallet on the register, or pushing a banknote into it, as in vanilla.

1. **The payer's game** predicts. The register shows the cash, the add-cash sound plays and the
   wallet shows less. It sends `ServerboundShopDepositPacket`: the shop, plus the banknote's NetId, or
   0 for the wallet.
2. **The server** checks that the payer is within reach of the register (`PlayerCanReach`, as service
   registers do) and that something is owed.
   - Wallet: it takes `min(owed, wallet)` from the wallet, as `Wallet.TrySpend` does.
   - Banknote: it spends the whole note, as `Money.TrySpend` does. This reuses the checks that
     `ServerboundMoneyItemDepositPacket` makes today (the note is live, the payer holds it, it isn't in
     Lost and Found), but credits the register instead of the wallet.
   - It adds the money to the register's deposit and broadcasts with the event `CashAdded`.
3. **Refused** (nothing owed, empty wallet, a note someone else holds): the prediction is undone.

The register's deposit isn't anyone's. It is cash in the till. Any player can add to it, and it
pays for the whole basket.

### Buying

The server's transaction, in one tick:

1. **Request.** `ServerboundShopBuyPacket` carries the shop and the basket revision the presser saw.
   The presser must be within reach of the register. If the revision is stale (someone scanned in the
   meantime), the buy is refused with `BasketChanged`: the presser sees the new basket and presses
   again. Nobody pays for a basket they didn't see.
2. **Lines are checked again.** Things change between scan and Buy: a licence is lost, a
   player buys the same item at another shop, a garage is unlocked. A line that would now go over its
   owner's stock is cut down or dropped, with the event `LineDropped` and the reason. If nothing is
   left, the buy is refused with `BasketEmpty`. A line whose owner has disconnected stays: it's
   delivered to their Lost and Found.
3. **Money.** `total ≤ deposited`, or the buy is refused with `NotEnoughCash` and the
   not-enough-money sound plays for everyone. The cash is exact: the server holds it, so there's no
   $0.50 tolerance.
4. **Commit.**
   - The total leaves the deposit, and the change is printed as a banknote by the server.
   - Each line becomes delivery entries (one per unit) with an owner, reserved against its owner's
     stock straight away.
   - The basket empties, the register enters `Delivering` ("processing"), and the event `Bought`
     goes out (buy sound, printer, "goodbye" when the delivery ends).
   - The receipt is printed (see [Receipts](#receipts)).
   - It is logged as one transaction, with an id, the presser, the payers (who deposited what), and
     the lines and their owners. `ItemChangeLog` records each item's birth with the transaction id.
5. **Delivery,** one item every 0.5 s, per shop (other shops aren't blocked). For each item that comes
   due:
   - **Where:** the counter if its owner is online and within 200 m of `shop.itemSpawnTransform` (in
     absolute coordinates), otherwise the owner's Lost and Found as `PurchasedDeliveryFallback`.
   - **Birth:** the server instantiates the prefab, sets `BelongsToPlayer`, sets vanilla's `Restock`
     flag (so the save stays vanilla-readable), gives it its NetId, and registers it as an owned
     item born `Purchased`. All of that happens before the item is replicated, so no game ever sees it
     unowned. See [Owned items](#owned-items).
   - **Counter:** placed at rest at the spawn point. Item sync then shows it to everyone near.
   - **Lost and Found:** handed to the owner's Lost and Found inert. The owner's game shows a HUD
     note: "Delivered to your Lost and Found".
   - **The owner's game** gets `ClientboundShopPurchasedPacket`, for what belongs to that player
     alone: `UnlockablesManager.UnlockItem` in their own profile, and the boombox's popup (vanilla's
     `BoomboxLegalInfoTrigger`, run there and not on the server).
6. When the last item is out, the register goes back to `Idle`.

### Cancel

`ServerboundShopCancelPacket`, from anyone within reach. It empties the basket, prints the whole
deposit as a banknote, and plays the cancel sound everywhere. Cancel can't interrupt `Delivering`.

### Cash nobody is using

- **Walking away:** every 2 s, if a register holds cash and no player is within 20 m of it, the cash
  goes back to the wallet (vanilla's `ReturnMoneyToPlayerCheck`, measured from every player). The
  basket stays.
- **Leaving the shop:** when no player has been within 200 m for 60 s, the basket empties too
  (vanilla's `OnDisable` cancel, which happens when the register unloads with the player gone).
- **Disconnecting** leaves the cash in the till. The walk-away rule returns it.
- **Server stop:** every register's cash goes back to the wallet before the final save.

### Receipts

The server prints the receipt, an unowned world item (paper, not an owned item, like job papers).
Its lines (name, owner's name, units, unit price, line total, grand total) travel with the item as
its payload, the way job booklets carry their job. Every game renders it with vanilla's
`BookletCreator_CashRegisterReceipt.Create(data, …)`. Lost and Found doesn't collect it, and it's
saved like any world item.

## Per-player stock

```text
remaining(player, item) = allowed(item)
                        - owned(player, item)      live owned items that came from a shop
                        - reserved(player, item)   their lines in every basket, and deliveries not yet born
```

- **`allowed(item)`** is vanilla's `initialAmount`, with the same rules, evaluated live on the
  server:
  - 0 for `careerOnly` items outside Career;
  - 0 for the wireless MU controller until the company holds the MU licence;
  - 0 for a garage key once that garage is unlocked (vanilla only checks this at load).
- **`owned(player, item)`** counts the player's live ledger entries with that prefab that are
  *shop-stocked*: vanilla's `Restock` flag is set on the item. It counts wherever the item is: hand,
  inventory, world, a train, a container, Lost and Found, installed on a car.
- **Restocking is implicit.** When an entry ends (binned, used up, destroyed), the count drops and
  the player can buy another. A used garage key (`PadlockKey.KeyUsed`) clears the flag and stops
  counting, as in vanilla. A GameObject being destroyed for another reason (a container deposit, a
  client projection retiring) is not the item's end, so it changes nothing. The server turns off
  `GlobalShopController.Restock`, `ShopRestocker.OnDestroy`'s call to it, and
  `UpdateItemStocksOnGameLoad`. No stock counter exists, so there is none to save or get wrong after
  a load.
- **The player's own view.** The server sends each player `ClientboundShopAllowancePacket` (their
  `remaining` and `allowed` per item) at join and whenever either changes for them. Their game
  writes it into vanilla's table (`purchasedItems = allowed - remaining`) so vanilla's checks and
  the scanner's "in basket / in stock" work unchanged. The server never reads vanilla's table.
- **What everyone sees:** the shelf tags show only what is true for every player: the price, or "Not
  available" for career-only items outside Career. They never say "Sold out" (it isn't sold out for
  everyone). A player's own limit shows on the scanner while they hold it (see
  [The scanner](#the-scanner)), on the register's info line ("limit reached" when a scan is refused),
  and in the refusal on their HUD.
- **Migration:** an old save's bought items have `Restock` set already. They count for whoever the
  owned-items migration makes their owner. The host's old vanilla purchases are unowned (gap #3) and
  count for nobody until someone owns them. `Shop_item_amount_data` stays untouched (vanilla ignores
  it too).
- **Until [PR 6](#6-per-player-stock)** the server keeps vanilla's shared table, and PR 2 sends each
  player `allowedToHaveAmount - purchasedItems` instead of `purchasedItems` (so licence limits show,
  #7). That is today's behaviour, with the bugs PR 6 removes.

## What each player sees

Everything in the world is the same for everyone. Only a player's own HUD and profile are theirs.

| Thing | How it's kept the same |
| --- | --- |
| Which display is where on the shelves | the server's seed per `ShopShelves`, sent at join |
| Shelf tags (name, price, "Not available") | the catalogue from the server |
| The register's screens (cash, total, prompt) | the register state: vanilla's `CashRegisterWithModulesTextController` reads the module units and `DepositedCash` the state wrote |
| Register sounds (scan, add cash, buy, cancel, not enough money) and the printer | events in the register state, played at the register by every game |
| Change and cancel banknotes | server-made items |
| The receipt | a server-made item with its lines |
| Bought items on the counter | server-made items, through item sync |
| The scanner's laser, screen and sounds, whoever holds it | [The scanner](#the-scanner) |
| The item's description notification | the holder's HUD only |
| "Delivered to your Lost and Found", refusal reasons | the player's HUD only |
| Boombox popup, unlocks in the spawn menu | the buyer's game and profile only |

## The scanner

The scanner is an item, so item sync already shows who holds it. On top of that:

- **Laser:** on every game while anyone holds it (vanilla: grabbed and big inventory closed; others
  can't see the big inventory, so "held" is enough).
- **Aim:** the holder's game sends `ServerboundShopScannerAimPacket` when the aimed display changes
  (shop, module or none), at most 10 a second. The server relays it to players within 50 m of the
  shop, adding the holder's own `remaining` for that item. Every game then shows the same thing on
  the scanner's screen (name, holder's units in basket / holder's remaining, price) and plays the
  hover sound at the scanner.
- **Scan and error sounds:** from the register events above.
- **Description notification:** stays on the holder's HUD only.

## Optimistic interaction

A friend plays at 250 ms. Every action they take at a shop shows at once on their own game and is
confirmed or undone when the server answers.

| Action | Predicted on the actor's game | Confirmed by | When refused |
| --- | --- | --- | --- |
| Scan | the line on the register, scan sound, scanner count | register state with the request acknowledged | line removed, error sound, reason on the HUD |
| Pay | register cash, add-cash sound, wallet balance | register state, money update | cash and wallet restored, reason on the HUD |
| Buy | buy sound, basket cleared, "processing" | register state `Delivering` | basket and cash restored, not-enough-money sound if that was the reason |
| Cancel | basket and cash cleared, cancel sound | register state | restored |
| Item delivery | nothing: items come from the server | item sync | — |

Delivery isn't predicted. Vanilla already spaces items 0.5 s apart, so the first one arriving a
round trip later is within what players expect. Predicting an item means making a local object and
swapping it for the server's, the adoption mess owned items is removing.

**How it works.** Each request carries a `RequestId` (a per-player counter). The client keeps its
pending requests as an overlay on the last register state it got from the server, and what it shows
is the state with the overlay applied. The server's state packet acknowledges the newest request it
applied from each player. A refusal names the request (`ClientboundShopRequestResultPacket`). An
acknowledged or refused request leaves the overlay. A request with no answer within 5 s is dropped
and the client shows the server's state, which is where it would have ended anyway. Sounds play once:
the actor's game played its predicted sound, so it skips the sound of an event it caused.

Conflicts are rare and settle in the server's order. Two players scanning at once both get their
lines. A Buy pressed while someone else scans is refused as `BasketChanged`, and the presser sees the
new basket. Nothing that was predicted can leave a game out of step for longer than one round trip.

## Player hosts and dedicated hosts

- **The host's own player** uses exactly the same path: its game's shop view sends the same requests
  through `LocalPlayerSession.Submit`, and the server answers in-process. Nothing in the shop checks
  `IsHost` to let the host's player act directly. Vanilla's register on the host is a view like a
  client's. The server's state lives in `ServerShopRegister`, not in vanilla's objects. On a player
  host they are the same `GlobalShopController` and registers, and the view writes into them while
  the server never reads them.
- **A dedicated host** has no player, no `PlayerManager.PlayerTransform` and no view. The server never
  uses `PlayerManager`: distances are measured from `ServerPlayer` positions, delivery from the
  owner's. The register objects may be inactive on a dedicated host; the server only reads
  `Shop.itemSpawnTransform`, the register's printer and money anchors, and the shelf seeds, which are
  valid either way.
- **Everything is tested on both.** Steps that need two players at once (one sees what the other
  does) need the host's own player, so they run on a player host only, and each scenario says so. On a
  dedicated host the same steps compare the client with the server's state.

## Late joiners

At `ReadyForWorldState` a joining player gets the catalogue and shelf seeds, every register's state
(about ten shops, a few lines each), and their own allowance. Items already delivered are items.
Deliveries still queued appear as they're born. A player who joins mid-purchase sees the register in
`Delivering`, and the scanner's aim the next time it changes.

## Persistence

The vanilla save stays the server's store ([decided 2026-10-19](server-player-split.md#save-format)).

- **Nothing new is saved for shops.** Stock is derived from owned items. Baskets and shelf layouts
  aren't saved in vanilla either.
- **Cash in a till isn't lost to a crash.** When the server writes the save, it writes the wallet plus
  every register's deposit as the money (vanilla's save would miss cash in a register). On load it's
  in the wallet.
- **Deliveries in progress** are finished before the save is written: any item still queued is born
  at once, on the counter or in Lost and Found by the usual rule. A save never holds a paid-for
  item that doesn't exist.
- **Bought items** keep vanilla's `"Restock": true` in their save data, so the save still loads in
  single player with vanilla's stock rules.
- **Profiles:** unlocks and the boombox flag stay in each player's own profile, as vanilla keeps them.

## Desync and rollback

- **Registers** carry a revision. A game applies only a newer one, and every state packet is the
  whole state (it's small), so a lost or reordered packet heals with the next one. A reconnect gets
  the full state.
- **Allowances** carry a revision per player, sent whenever their count changes (an owned-items
  event).
- **Money** moves to `double` with a revision (#16).
- **Shelves** come from the seed. A client that laid its shelves out before the seed arrived lays
  them out again.
- **`dvmp_desync`** gets a shop entry: each register's lines and deposit, and each player's allowance,
  compared between the server and every game.
- **Observability:** `DebugTrace` events `shop.scan`, `shop.deposit`, `shop.buy`, `shop.refused` (with
  reason), `shop.delivered` (where and why); the transaction id is in every one.

## Packets

Every serverbound packet goes through the server's request queue and is in `NetworkClient`'s request
list. `LocalPlayerSession` submits the same packets in-process.

| Packet | Fields | When |
| --- | --- | --- |
| `ServerboundShopScanPacket` | `RequestId`, `ShopIndex`, `ModuleIndex`, `ScannerNetId` | a scan |
| `ServerboundShopDepositPacket` | `RequestId`, `ShopIndex`, `MoneyNetId` (0 = wallet) | paying |
| `ServerboundShopBuyPacket` | `RequestId`, `ShopIndex`, `Revision` | Buy |
| `ServerboundShopCancelPacket` | `RequestId`, `ShopIndex` | Cancel |
| `ServerboundShopScannerAimPacket` | `ScannerNetId`, `ShopIndex`, `ModuleIndex` (255 = none) | the aimed display changes, ≤ 10/s |
| `ClientboundShopCataloguePacket` | prefab names, prices, availability, each shop's module checksum, shelf seeds | join |
| `ClientboundShopRegisterStatePacket` | `ShopIndex`, `Revision`, lines (`ModuleIndex`, owner `PlayerId`, units), `Deposited` (double), phase, last event (kind, actor, module, scanner), acknowledged `RequestId` per player | every change, to everyone in the world state |
| `ClientboundShopRequestResultPacket` | `RequestId`, result, reason | a refusal, to the requester |
| `ClientboundShopAllowancePacket` | `Revision`, per catalogue item: `remaining`, `allowed` | join and every change, per player |
| `ClientboundShopScannerAimPacket` | `ScannerNetId`, `ShopIndex`, `ModuleIndex`, holder's `remaining` | relayed aim, to players within 50 m |
| `ClientboundShopPurchasedPacket` | the items (prefab, NetId or "Lost and Found") | to the owner when each is born |

Removed: `ServerboundShopPaymentPacket`, `ServerboundShopPurchasePacket`, `ClientboundShopStockPacket`.
`ClientboundMoneyPacket` changes to `double` with a revision. Each protocol change updates the
protocol fingerprint (`docs/testing.md`).

Refusal reasons: `NotNear`, `NoScanner`, `NotSoldHere`, `NotAvailable`, `NeedsLicence`,
`GarageUnlocked`, `LimitReached`, `NothingOwed`, `WalletEmpty`, `MoneyNotYours`, `NotEnoughCash`,
`BasketEmpty`, `BasketChanged`, `Busy`.

## Owned items

The seam with [`owned-items.md`](owned-items.md) (PR #275), agreed with that session on 2026-10-21
and written into both docs:

1. **Shops hands over a finished item.** The server makes it and registers it with the ledger before
   any player sees it:
   - owner: the identity that scanned the line;
   - born `Purchased`, `by` that identity;
   - detail: the shop, the transaction id, and who paid.
2. **It is born in one of two places:** `World` on the counter, or the owner's Lost and Found
   through owned items' one entry point, `SendToLostAndFound(id, PurchasedDeliveryFallback)`.
3. **Shops reads the ledger** for per-player stock:
   - `shopStocked` on each entry mirrors vanilla's `Restock` flag on the item. It's set at birth,
     cleared where vanilla clears it (a used garage key), read from the stored state at load and
     migration, and never set on replacements;
   - `CountLive(owner, prefab, shopStocked)` gives `owned(player, item)`;
   - a change event, raised when an entry is born, ends, or has `shopStocked` change, triggers that
     player's allowance packet.
4. **Owned-items PR 5's first bullet**, the host's own player buying through `SpawnShopPurchase`,
   is done by shops [PR 2](#2-the-register-on-the-server), where the host's player goes through the
   same requests. Owned-items PR 5 keeps the removal of adoption. Shop receipts are server-made from
   [PR 3](#3-delivery-receipts-and-the-buyers-own-effects), so nothing in a shop adopts.
5. **Receipts aren't owned items.**
6. **The shops plan waits on owned items at three points:**
   - [PR 2](#2-the-register-on-the-server) needs owned-items PR 1 (the ledger), to register each
     purchase as born `Purchased`. Until owned-items PR 2 (ownership by identity), the owner is also
     set in today's registry (number plus GUID), as `SpawnShopPurchase` does now.
   - [PR 3](#3-delivery-receipts-and-the-buyers-own-effects) needs owned-items PR 3 (Lost and Found
     for everyone), which brings `SendToLostAndFound` and Lost and Found records for offline owners.
     Today a Lost and Found record needs a non-zero owner number (`LostAndFound.cs:140-142`), so an
     offline buyer's delivery has nowhere to go.
   - [PR 6](#6-per-player-stock) needs owned-items PRs 1 and 2.

## Gadgets

For [`gadgets.md`](gadgets.md) (PR #273):

- Installed gadgets count against their owner's stock, as vanilla counts `StorageInstalledGadgets`.
- The wireless MU controller needs the MU licence to buy. Installing and using it don't check the
  licence in vanilla. The server checks the company's licence at scan and again at Buy.
- Consumption needs nothing from the shop. When the gadget design's server-side replacement scope
  ends an item's entry (duct tape, solder spools, labels), the slot frees. A replacement (the empty
  roll) is born `Replaced` and isn't shop-stocked.

## Service-station registers

Out of scope. They keep `NetworkedCashRegisterWithModules` and `ServerCashRegister`. This work only
removes the `IsShopRegister` branches from the shared register code, so `pit-stop` and
`host-register-empty-basket` must still pass after PR 2.

## Plan

Small PRs from `beta`, in order, each on a branch `shops/<what>`. Each one:

- runs its scenarios on a player host and a dedicated host
  (`RUNS="<scenario> @player; <scenario> @dedicated"`), plus `shop`, `pit-stop` and
  `host-register-empty-basket`;
- adds its rules to `Multiplayer.Core.Tests/Shops/`;
- updates this doc's status and `world-sync.md`'s shops section;
- keeps every file under the size limit (`NetworkServer.Stations.cs` loses its shop code to
  `NetworkServer.Shops.cs`).

Scenarios are C# in `DevTools/DvmpScenarios/Scenarios/` ([scenarios.md](scenarios.md)), using
`Places.Shop`. A pair has one client, so "another player" is the host's own player, and a step that
needs two players runs on a player host only (`[Needs(Need.PlayerHost)]`), as owned-items does.

### 0. Only if #13 is confirmed: no restock on a retirement

If the check in [To check in game](#to-check-in-game) shows a container deposit restocking, the server
skips `Restock` when the item's removal cause isn't an end (`DepositedInContainer` and the other
retirements). Small, and it goes first because it's a dupe. PR 6 replaces it.

- **`shop-restock`** (new): the client buys a shovel until the stock is out, deposits one in a
  toolbox, and the stock stays out on both games. It bins one, and the stock comes back.

### 1. The same shelves everywhere

The server picks a shelf seed per session and sends it at join (`ClientboundShopLayoutPacket`).
Every game derives each shop's verified seed from it and lays its shelves out the same way. The client
sends a fingerprint of its shops with `ReadyForWorldState`, and a client whose shops don't match is
refused. The catalogue's prices and availability come with PR 2.

- **`shop-layout`** (new): every display's position at every shop matches between the host and the
  client, within 1 cm.

### 2. The register on the server

The core of it. After owned-items PR 1 (the ledger).

- `ServerShopRegister` owns the basket, with lines by owner, the deposit and the phase.
- Scan, deposit, buy and cancel are requests. Each game's register is a view of the server's state:
  the screens, sounds and printer are the same everywhere.
- The host's own player goes through the same requests. Owned-items PR 5's first bullet is done
  here.
- Buy commits on the server. Owners are set by line, and delivery is today's `SpawnShopPurchase`,
  owned from birth.
- The walk-away, leave-the-shop, disconnect and server-stop rules. The register's cash is added to the
  saved money.
- The server checks that the shop sells the item, the licence and the garage. Clients get
  `allowedToHaveAmount - purchasedItems` (#7).
- Money becomes `double` with a revision.
- Removed: the per-player escrow, the payment and purchase packets, and the `IsShopRegister`
  branches.

Fixes #1, #3–#9, #16, #18. No prediction yet: the actor waits a round trip, as today.

- **`shop`** (changed): the client buys. The wallet pays once, the item has one NetId and its owner
  is the client, the stock drops on both, and the cancelled basket returns its cash. Plus
  `WHO=host` (player host only): the host's own purchase is owned by the host before anyone touches
  it.
- **`shop-basket`** (new): the client scans two items and pays, and the server's and client's
  registers match (lines, owners, deposit, revision). On a player host the host's player sees the
  same basket and hears the scans. The host's player scans a line, the client presses Buy, and each
  owns their own line.
- **`shop-cash`** (new):
  - the client pays and walks 25 m away, and the cash is back in the wallet on both;
  - the client pays and disconnects, and the cash comes back;
  - the client pays and the server saves, and the save's money includes the till.
- **`shop-licence`** (new): without the MU licence the client can't scan the wireless MU
  controller (`NeedsLicence`). With it, the client buys it.

### 3. Delivery, receipts and the buyer's own effects

After owned-items PR 3 (Lost and Found for everyone, with `SendToLostAndFound`).

- Delivery checks each item's owner when it comes due: the counter within 200 m, otherwise their Lost
  and Found (`PurchasedDeliveryFallback`), with the HUD note. Only the buying shop is blocked.
- Server-made receipts carry their lines, and server-made banknotes carry the change.
- `ClientboundShopPurchasedPacket` runs the unlock and the boombox popup on the owner's game. Nothing
  runs `APurchaseTrigger` on the server.

Fixes #10–#12.

- **`shop-delivery`** (new): the client buys five items (three lanterns, two lighters) and
  fast-travels right after Buy. The ones not yet out are in the client's Lost and Found as `PurchasedDeliveryFallback`.
  The variant `LEAVE=disconnect` disconnects instead, and they're there when the client rejoins.
- **`shop`**: the receipt exists on both games with the same lines. The change banknote is one item on
  both. On a player host, a client's boombox shows no popup on the host.

### 4. Prediction

The client's overlay, `RequestId`s, acknowledgements and refusals with reasons on the HUD.

- **`shop-lag`** (new, `LAG=250`):
  - a scan shows on the client's register in the frame it's made, and stays after the server answers;
  - a refused scan (`LIMIT=1` and a second scan) disappears within 600 ms with the error sound;
  - paying and Buy show at once, and the client's view equals the server's after 1 s;
  - with the host's player scanning at the same moment, the client's Buy gets `BasketChanged` and
    nobody pays for a basket they didn't see.

### 5. The scanner

The laser on held scanners, aim relay, the scanner's screen, and hover, scan and error sounds for
remote holders.

- **`shop-scanner`** (new, player host only): the client aims at a display, and the host's game shows
  the client's scanner laser and its screen naming that item. The client scans, and the host hears it
  at the scanner. On a dedicated host: the server's record of the scanner's aim matches the client's,
  and the client hears its own scan once, not twice.

### 6. Per-player stock

After owned-items PR 1 and PR 2.

- `remaining` comes from the ledger, and allowance packets go to each player.
- Vanilla's restock and load recount are turned off. Shelf tags show the price or "Not available",
  never "Sold out". PR 0 is removed.

Fixes #13, #14, #17.

- **`shop-stock`** (new):
  - the client buys shovels up to their limit, and the next scan is refused with `LimitReached`;
  - on a player host, the host's player can still buy one;
  - the client bins one, and can buy again;
  - the client deposits one in a toolbox, and still can't;
  - save and reload: the same `remaining` on both;
  - a made-up offline identity's shovel doesn't count for the client.

## To check in game

These come from reading code. Each is a short `dvmp_eval` on one pair, by the coordinator. None was
run for this doc.

| # | Check |
| --- | --- |
| 2 | Confirmed 2026-10-21 by `shop-layout` on `beta`: 38 of 244 displays apart. |
| 7 | The licence limit doesn't apply on a client: without the MU licence, read the MU controller row's `allowedToHaveAmount` on host and client. |
| 10 | A client's receipt is local: after a client purchase, look for a `Receipt` item with a NetId on the host. |
| 11 | A client's boombox purchase shows the popup on a player host. |
| 13 | Confirmed 2026-10-21 by `shop-restock` on `beta`: stock 11 -> 12 after a lighter went through a toolbox. |
| 14 | Stock comes back on load: own a shovel in a cold container or an offline player's inventory, save and reload, read `ItemsInStock`. |
| — | Shop registers are active on a dedicated host (`CashRegisterWithModules.isActiveAndEnabled` per shop), which says whether vanilla's own `OnDisable` cancels baskets there today. |
| — | `GlobalShopController.InitializeShopData` on a client sees the server's game mode (sandbox prices are 0 on a client of a sandbox server). The catalogue packet makes this moot from PR 1. |
| — | The current stock table: prefab, `initialAmount`, `basePrice`, `careerOnly`, to fill in the research doc. |

## Open questions

Choices made in this doc that the user may want to change:

- **Cancel empties everyone's lines.** The alternative is that Cancel removes only the presser's
  lines, and returns cash only when the basket ends up empty.
- **Change as a banknote,** as in vanilla. The alternative is straight back to the wallet, which is
  what the mod does today because a banknote used to exist on one game only.
- **Companies.** Money, licences and stock are "the company's" once companies exist. With one company
  holding everyone (today), that is the shared wallet and licences. Later, the deposit would track
  which company paid what, so change and walk-away cash go back to the right company.
