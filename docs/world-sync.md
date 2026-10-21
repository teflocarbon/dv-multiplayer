# World and environment sync

How the state of the world that isn't rolling stock, items or jobs is kept the same for every
player: the clock, weather and sleep, difficulty settings, the shared wallet, shops, service
stations, junctions and turntables, switches, explosions, hazmat ground tiles and the sandbox cargo
loader. Trains are in [design/trains.md](design/trains.md), items in the `design/item-*.md` docs and
[design/container-storage.md](design/container-storage.md), jobs and warehouse machines in
[jobs.md](jobs.md). The overall model (host commits, clients send intent) is in
[architecture.md](architecture.md).

Paths below are relative to `Multiplayer/`. "The host" is the game running `NetworkServer`; its
own player is also a client and goes through the same request paths unless noted.

| System | Authority | Synced on join | Status |
| --- | --- | --- | --- |
| Clock and weather | Host | Yes (`ReadyForWorldState`) | Synced every 30 s |
| Sleep cooldown | Host | Yes (`ReadyForWorldState`) | Host checks every client sleep |
| Fast travel time skip | Requesting player | n/a | Relayed, not validated |
| Difficulty (`GameParams`) | Host | Yes (`ReadyForGameData`) | Synced on every change |
| Wallet, licences, garages | Host | Yes (save-game packet) | Synced; some client spends not forwarded |
| Debt | Host | Yes (save-game packet) | Flag only; an admin client pays all fees at once |
| Shops | Host (registers, money, stock, spawn) | Yes (`ReadyForWorldState`) | Synced: one server-owned register per shop |
| Service stations | Host | On entering the station's range | Synced; station interactions not validated |
| Junctions, turntables | Last writer | Yes (`ReadyForWorldState`) | Relayed, not validated |
| Generic switches | Host | Yes (`ReadyForWorldState`) | Synced, reach-checked |
| Drilled holes in the world | Host | Yes (`ReadyForWorldState`) | Join only |
| Explosions | Host | Exploded flag in train baseline | Synced |
| Car cargo fires | Host | Yes (`ReadyForTiles`) | Synced; client lighters and flares validated |
| Hazmat ground tiles | Host | Yes (`ReadyForTiles`) | Synced every 12 ticks |
| Sandbox cargo loader | Host | Via train baseline | Synced, validated |
| Comms radio damage and derail | Host | Via health and train sync | Synced, validated |

## Join order

A joining client walks through `PlayerLoadingState`
(`Shared/Protocol/Data/PlayerLoadingState.cs`); `NetworkServer.OnServerboundLoadStateUpdatePacket`
sends world state at these steps:

| State | Sent by the host |
| --- | --- |
| `ReadyForGameData` | `ClientboundGameParamsPacket`, `ClientboundSaveGameDataPacket` (wallet, licences, garages, debt flag, job clock) |
| `ReadyForWorldState` | `ClientboundWeatherPacket`, `ClientboundSleepStatePacket`, `ClientboundShopStockPacket`, `ClientboundShopLayoutPacket`, `ClientboundRailwayStatePacket`, one `CommonGenericSwitchStatePacket` per switch, `ClientboundCustomizationsPacket` |
| `ReadyForTrainSets` | Train baseline, which carries each car's exploded flag and cargo |
| `ReadyForTiles` | Every hazmat tile (`HazmatServer.SendAll`) |

At `ReadyForWorldState` the host first checks the client's track graph and kicks it with a reason
if it differs ([design/track-graph-check.md](design/track-graph-check.md)). The player is then
added to the server's `peers` table, and every broadcast
(`SendPacketToAll`) only walks `peers`. Anything broadcast between `ReadyForGameData` and
`ReadyForWorldState` (while the client loads the scene) never reaches that client; see Known gaps.

If the host's save is still loading, a `ReadyForGameData` request is parked in `gameDataQueue` and
answered from `OnLoaded`. Before this, a joiner got the default difficulty (map markers, fast
travel and so on all allowed) and kept it for the session. `ReadyForWorldState` requests made while
the host is loading go to `joinQueue` and the client gets `ClientboundServerLoadingPacket`.

## Clock, weather and sleep

### Weather and clock

The host's `WeatherDriver` save data (game clock as an OADate, weather offset, wetness, starting
weather) goes out as `ClientboundWeatherPacket`: to a joiner at `ReadyForWorldState`, and to every
player at `ReadyForWorldState` or later every 30 s (`WEATHER_UPDATE_INTERVAL` in
`NetworkServer.OnTick`). Clients apply it with `WeatherDriver.LoadSaveData`
(`NetworkClient.OnClientboundWeatherPacket`).

The host's clock arrives one trip late. Applying it as-is stepped the client's clock back a little
on every update, and time-driven consumers read a backward step as a whole day passing (a lit
flashlight's battery emptied at once). Once loaded, a client now keeps its own clock when it is
ahead of the host's by less than 300 game seconds (`MaxWeatherClockLeadSeconds`). Bigger
differences, such as the host setting the time, are applied.

### Fast travel time skip

Two prefixes on `TimeAdvance.AdvanceTime`, each doing nothing outside its role: `Client/World/TimeAdvancePatch.cs` on a
remote client, `Server/World/TimeAdvanceServerPatch.cs` on the host. With other players
connected, fast travel only skips time when the host's `FastTravelAdvancesTime` mod setting is on
(the server's `NetworkServer.FastTravelAdvancesTime`; sent to clients inside `ClientboundGameParamsPacket`, and re-sent when the host changes it). The
player who travelled advances locally and sends `ServerboundTimeAdvancePacket` (the host submits it to the server in-process). The server advances
its own world's clock for a client's request (the host's own has already advanced), then relays it
to everyone else as `ClientboundTimeAdvancePacket`, never to the host's own client
(`NetworkServer.HandleTimeAdvance`). An advance applied for another player is not sent on again
(`IsProcessingPacket`, which includes the server's `ApplyingRemoteChange` scope), so the traveller
doesn't skip twice. The host checks only the setting, not the amount. The `/time` chat command
advances the server's clock the same way and sends the skip to every player.

`Client/UI/FastTravelUIControllerPatch.cs` disables "fast travel with loco" while others are
connected.

### Sleep

Vanilla `BedSleepingController` allows a sleep only once `GetMinimumTimeSinceLastWakeSeconds` has
passed since `lastWakeTime`. Each game used to check its own wake time (a client's started empty on
every join), so players could sleep one after another and skip as much time as they liked, making
everyone's jobs late. There is now one cooldown, kept by the host.

- The host's `lastWakeTime` and `lastSleepDurationSeconds` go to clients as
  `ClientboundSleepStatePacket` (`LastWakeTimeOADate`, `LastSleepDurationSeconds`): on join and
  after every accepted sleep. A client's bed menu therefore shows the shared cooldown.
- A sleeping player skips time locally and sends `ServerboundTimeAdvancePacket` with `IsSleep`
  set (both patches tell sleep from fast travel by `BedSleepingController.IsSleeping`).
- `NetworkServer.HandleSleep` ignores amounts outside (0, 24 h]. The host's own sleep has
  already passed vanilla's check and advanced the host's clock, so it is relayed and the new sleep
  state broadcast. A client's sleep is rejected when `GameParams.SleepCooldownInHours < 0` or the
  shared cooldown hasn't run out; the host then sends that client the sleep state and the weather,
  whose clock is far behind the client's and puts it back. An accepted sleep advances the server's
  clock (in `ApplyingRemoteChange`), is relayed to everyone else, the host sets `lastWakeTime` to
  now plus the skip, and broadcasts the sleep state.

`Client/World/BedSleepingControllerPatch.cs` stops sleep from turning off locos and setting
handbrakes (`TurnOffAndEngageHandbrakeOnAllLocos`, `EngageHandbrakesOnCurrentTrain`) unless the
host is alone, so one player's nap doesn't stop someone else's train.

### Pause

`Shared/Session/AppUtilPatch.cs` blocks `AppUtil.RequestPause` and the time-scale change unless
`DvExtensions.AllowPause()` (single player, or the host alone). `GraphicsOptionsPatch.cs` forces
`Application.runInBackground` in the same case, so an alt-tabbed host keeps simulating.

## Difficulty and game parameters

`Server/World/GameParamsPatch.cs` postfixes `GameParams.OnPropertyChanged` on the host and calls
`NetworkServer.SendGameParams`, which broadcasts `ClientboundGameParamsPacket` (the whole
`GameParams` as `JsonUtility` JSON, plus `FastTravelAdvancesTime`) to players at `ReadyForGameData`
or later.

`ClientboundGameParamsPacket.Apply` snapshots every public read/write property, overwrites the
object with `JsonUtility.FromJsonOverwrite`, and raises `OnPropertyChanged` for each property that
changed. `FromJsonOverwrite` raises no events itself, so listeners (map markers and fast travel,
comms radio modes) used to keep the value they had seen. The client applies the packet to both
`Globals.G.GameParams` and `Globals.G.gameParamsInstance`.

The joiner's difficulty preset (`SerializedDifficulty` in `ClientboundSaveGameDataPacket`) is used
to build its `StartGameData_ServerSave` (`Client/Session/StartGameData_ServerSave.cs`).

## Wallet, licences and debt

### Wallet

There is one wallet, the host's `Inventory.PlayerMoney`. `Server/Career/
NetworkedSaveGameManager.cs` listens to `Inventory.MoneyChanged` on the host and broadcasts the new
balance as `ClientboundMoneyPacket` (a `double`, as the game keeps it, with a revision: it is sent
unordered, and a client keeps the newest); clients `SetMoney` it. A client's own balance is a mirror:
anything it adds or removes locally is overwritten by the host's next update, so every way a client
spends or earns money has to reach the host:

- Banknotes and coins: `Client/Items/MoneyPatch.cs` replicates a note's value as a
  host-authoritative tracked value, and `Money.TrySpend` on a client (picking it up, stashing it,
  paying a register) first sends `ServerboundMoneyItemDepositPacket`. The host ignores items that
  aren't live money and rejects notes in Lost and Found, in a container, or in another player's
  hand or inventory; otherwise it spends the note, credits the wallet and destroys the item.
- Shops and service-station registers: below.
- Licences: `CareerManagerLicensePayingScreenPatch.cs` sends
  `ServerboundLicensePurchaseRequestPacket`. The host looks the licence up, refuses while there
  are debts or when the wallet is short, then takes the price and acquires it. Rejections send
  nothing back.
- Comms radio services (rerail, delete, crew vehicle) go through host requests; see
  [design/trains.md](design/trains.md).

Licences and garages acquired on the host are broadcast as `ClientboundLicenseAcquiredPacket` and
`ClientboundGarageUnlockPacket`. `UnlockablesManagerPatch.cs` runs `UnlockInArray` on the host only,
and `PhysicalLicensePaperPatch.cs` stops licence papers being created in multiplayer (licences are
entitlements, not items).

### Debt

`Client/Career/CareerManagerDebtControllerPatch.cs` runs `RegisterDebt` and `UnregisterDebt` on
the host only. Clients only know whether there is debt: `NumberOfNonZeroPricedDebts` returns 1 or 0
from `CareerManagerDebtControllerPatch.HasDebt`, set from the save-game packet on join and from
`ClientboundDebtStatusPacket` afterwards. `Server/Career/
NetworkedCareerManagerDebtController.cs` sends that packet when "has debt" changes (it used to go
out only at exactly 0 or 1 debts, so going from none to two left clients thinking there was none).
It checks on `DebtListsUpdated` and after every `RefreshExistingDebtsState`
(`CareerManagerDebtControllerServerPatch.cs`): a refresh moves a debt between the zero-priced and
priced lists, a loco taking damage or being refuelled, without raising `DebtListsUpdated`, so a
loco damaged after a client joined left its fees screen saying there were no fees. Vanilla
refreshes every 60 s, and whenever the fees are read.

A client has no fee list, so `CareerManagerFeesScreenPatch.cs` replaces its fees screen with "Pay
all fees?". Confirming sends `ServerboundPayFeesRequestPacket`, and the server pays them for an
admin as `/payfees` does (`ChatManager.PayFees`, `FeePayment`): the cheapest first until the
insurance quota is reached, then insurance clears the rest. The outcome goes out in chat.

Paying a loco's debt calls `SimulatedCarDebtTracker.TurnOffDebtSources`, which powers the whole
consist down and sets its handbrake. The host pays for every loco anyone has used, so this stopped
other players' trains. `Server/Career/SimulatedCarDebtTrackerPatch.cs` skips it when other
players are connected and any car of the consist is moving faster than 0.1 m/s or has a player
aboard; the repair and refill still happen.

## Shops

Vanilla shops are a `CashRegisterWithModules` per shop plus `GlobalShopController`, which spawns
bought items through `AddItemToInstantiationQueue`/`InstantiatePurchasedItems` and tracks stock as
`ShopItemData.purchasedItems`. In a session the server owns the registers, the money, the stock
and the items: no game's vanilla register decides anything. The design, and what is still to come,
is [design/shops.md](design/shops.md).

The register (`Server/Shops/ServerShops.cs`): one basket per shop (`Multiplayer.Core/Shops/ShopBasket.cs`)
that everyone at the shop shares. Each line is units of a shelf item owned by the player who
scanned them. The basket also holds the cash paid in and whether a purchase is being delivered,
and every change bumps its revision. A shop is its index in `GlobalShopController.globalShopList`
and a shelf item its index in the shop's `scanItemResourceModules` (the join check below makes
those agree).

What a player does at a shop register is a request, on every game, the host's own player's
included (`Client/Shops/ShopRegisterPatches.cs`, through `IShopsClient`: `NetworkClient.Shops.cs`,
`LocalPlayerSession.Shops.cs`):

| Player action (vanilla entry point) | Request | The server checks |
| --- | --- | --- |
| Scan (`ScanItemCashRegisterModule.AddItemsToBuy`) | `ServerboundShopScanPacket` | the item is the shop's, the player is within 6 m of it and holds a shop scanner, nothing is being delivered, the game mode, the MU licence, an unlocked garage, the stock (this basket's units included) |
| Pay with the wallet or a banknote (`MoneyUse.HandleUse`, `CashRegisterBase.OnTriggerEnter`) | `ServerboundShopDepositPacket` | reach, something is owed; the wallet pays what is owed (or what it has), a banknote all of it (`NetworkServer.TrySpendMoneyItem`, the checks a wallet deposit makes) |
| Buy (`CashRegisterWithModules.OnBuyPressed`) | `ServerboundShopBuyPacket` with the basket revision the presser saw | reach, not delivering, nobody else changed the basket since that revision (the presser's own scans and payment still on their way back don't count: `ShopBasket.ChangedByOthersSince`), then each line again (lines that can no longer be bought are cut or dropped), then the cash |
| Cancel (the Cancel button) | `ServerboundShopCancelPacket` | reach, not delivering |

A Buy takes the total out of the deposit and prints any change as a banknote at the register
(server-made, so everyone has it), adds to `purchasedItems`, unlocks the items and sends the stock.
The register is then "delivering" (vanilla's "processing" screen, Buy and Cancel refused) while
`Server/Shops/ShopDelivery.cs` puts the items on the counter one every 0.5 s, each owned by its
line's owner from birth. A Cancel empties the basket and prints all the cash as a banknote. A
refused request comes back to the player who asked (`ClientboundShopRequestResultPacket`), with the
reason on their HUD; a refused scan also plays the error sound for everyone.

Every change goes to every game, the host's own player's included, as the register's whole state
(`ClientboundShopRegisterStatePacket`: lines, cash, delivering, revision, and the event that caused
it), and to a joining player at `ReadyForWorldState`. `Client/Shops/ShopRegisterView.cs` writes it
into vanilla's modules and `DepositedCash`, so vanilla's screens show it, and plays the event's
sound at the register (buy, cancel, not enough money, add cash, the scanner's scan and error). The
presser's game prints the receipt, as before. Vanilla's own cash handling is off for shop registers
on every game: `ReturnMoneyToPlayerCheck` (`CashRegisterBasePatch.OnEnable`) and the cancel in
`OnDisable` (`CashRegisterWithModulesPatch`). `GlobalShopControllerClientPatch` turns vanilla's
purchase queue off. Shop registers aren't numbered as service registers
(`NetworkedCashRegisterWithModules`) any more.

Prediction: a player's own requests show on their game at once, so a player at 250 ms doesn't wait
for the round trip. Each request carries a request id (a counter on the player's game). Its
expected effect goes into the register's prediction (`Multiplayer.Core/Shops/ShopPrediction.cs`):
a scan's line, a payment's cash (what is owed, from the wallet, or the whole banknote), a Buy's empty
basket and "processing", a Cancel's empty basket. `ShopRegisterView` shows the server's last state
with those applied, in order, and plays the predicted sound at once (the scanner's scan sound, add
cash, buy and printer, cancel). The server's state names the request it answers (`AckPlayerId`,
`AckRequestId`): that request and any earlier ones leave the prediction, and their events' sounds
aren't played again. A refusal names its request too, so it leaves the prediction, and the register
shows the server's state again: the line disappears, and the error or not-enough-money sound plays.
A request with no answer within 5 s is dropped. The wallet isn't predicted: its balance follows the
server's money updates a round trip later. On the client, a scan this game can already see is out
of stock isn't sent (the scanner plays its error sound, as vanilla's own check did).

Cash nobody is using (`ServerShops.Tick`, every 2 s): once no player is within 20 m of a register,
its cash goes back to the wallet, as vanilla's `ReturnMoneyToPlayerCheck` did for the one player;
a player who walked off, fast travelled or disconnected gets it back the same way. A basket nobody
has been within 200 m of for a minute empties, as vanilla's cancel did when the register unloaded.
Vanilla saves the wallet plus every register's cash as the money. `Server/Shops/ShopTillSavePatch.cs`
writes the wallet, the service registers' cash and the server's tills instead, so a dedicated host
(whose vanilla registers show nothing) saves the till too, and a player host doesn't count it twice.

Stock: `NetworkServer.SendShopStock` sends `ClientboundShopStockPacket` (prefab names,
`purchasedItems`, and how many of each may exist) on join, after every purchase, after `Restock`,
and when a licence or garage is acquired. A client's own load never runs vanilla's rules that take
the wireless MU controller off sale without the MU licence, or a garage key once its garage is
unlocked, so it showed those as for sale; the server works them out live (`ServerShops.Allowed`)
and sends them. `Restock` runs on the host only, so a client destroying its copy of a bought item
doesn't restock. Vanilla's `ShopRestocker.OnDestroy` restocks on any destroy, but the server also
destroys a GameObject when the item lives on elsewhere: a cold-container deposit, an aborted
withdrawal's fresh copy, a replaced scene item. `Server/Shops/ShopRestockerRetirementPatch.cs`
skips the restock for those (they are marked `NetworkedItem.ColdStorageRetirement` first). Before
it, a bought lighter put through a toolbox put another on the shelf.

Shelves: vanilla's `ShopShelves.Start` seeds each shop's layout from `UnityEngine.Random`, so every
game put the shelf items in different places (38 of 244 displays at the shop by the shop scenario's
station, up to 5.6 m apart). The server picks a seed per session once its world has loaded
(`NetworkServer.InitialiseShopShelves`, in `Server/Session/NetworkServer.Shops.cs`) and sends it at
`ReadyForWorldState` (`ClientboundShopLayoutPacket`). `Shared/Shops/ShopShelfLayout.cs` makes every
game, the host's included, pick the shop's verified seed from it and the shop's index
(`Multiplayer.Core/Shops/ShopShelfSeed.cs`), and lays out again any shelves already laid out
before the seed arrived. Outside a session vanilla's random layout is unchanged.

Shop check at join: shops and their items are named by index, so the client sends a
`ShopCatalogueFingerprint` (`Multiplayer.Core/Shops/`: the stock table's prefabs, then each shop's,
in order) with `ReadyForWorldState`. The server refuses a client whose shops differ, as it refuses
a different railway ([design/track-graph-check.md](design/track-graph-check.md)).

Scenarios: `shop` (a player scans, pays and buys: the wallet pays once, one NetId on both, owned by
the buyer, stock drops; a cancelled basket's cash is one banknote on both; `WHO=host` for the host's
own player), `shop-basket` (the shared basket matches on server and client; each item is owned by
whoever scanned it), `shop-cash` (the save counts the till; walking away returns it; an abandoned
basket empties), `shop-licence` (the MU controller is off sale on a client without the licence),
`shop-layout`, `shop-restock` (a deposit through a container doesn't restock; destroying the item does),
`shop-predict` (at 250 ms the client's scan shows at once; a refused one rolls back). `shop` and
`shop-basket` take `LAG=250`.

## Service stations

A service station is a `PitStopStation` (car selector, resource levers, faucet), its plugs
(`PluggableObject` on a `PropHose`) and a `CashRegisterWithModules`. Code:
`Shared/Stations/NetworkedPitStopStation.cs`, `NetworkedPluggableObject.cs`,
`NetworkedCashRegisterWithModules.cs` (what both games run: ids, controls, applying a change,
sending the local player's requests through `IStationsClient`), their server parts in
`Server/Stations/` (`ServerPitStopStation`, `ServerPluggableObject`, `ServerCashRegister`: range,
validation, relays, the bulk update, the register's requests),
`Shared/Stations/PluggableObjectPatch.cs`, `Server/Stations/PropHoseRopePatch.cs` (the hose's
rope), `Client/Stations/{PluggableObjectClientPatch,PropHoseClientPatch}.cs` (no snapping home on a
client), `CashRegisterWithModulesPatch.cs`, `CashRegisterBasePatch.cs`.

Ids: stations and registers are numbered by sorting them by position
(`InitialisePitStops`, `InitialiseCashRegisters`, run on the host in `OnLoaded` through
`NetworkServer.InitialiseStations`, which also adds the server parts, and on the client in
`SyncWorldState`). Ids are not sent; both games must have the same objects.

Range: the host gives each station a `CullingManager` sized from the station's
`PlayerDistanceGameObjectsDisabler` (500 m by default), with the activation region at half the
squared distance. When a player enters it, the host sends `ClientboundPitStopBulkUpdatePacket`
(car count, selected car, faucet notch, each resource module's data, each plug's state) and the
register's deposited cash. That is the join sync for stations. Updates go only to players in the
activation region. A player leaves it after 3 s beyond the culling distance, or at once when the
host's disabler turns the station off (nobody is within 500 m), so coming back sends the state again.

Station controls: car selector, levers and faucet send `CommonPitStopInteractionPacket`. The host
applies it and relays it to the other players in range. `ValidateInteraction` is a TODO that
always returns true. While a resource flows the host sends its `unitsToBuy` every tick
(`ResourceUpdate`) and once or twice more after it stops, so the bill matches.

Plugs: `CommonPitStopPlugInteractionPacket` (`PickedUp`, `Dropped`, `DockHome`, `DockSocket`).
`NetworkedPluggableObject.ValidateInteraction` checks:

- the type isn't `Rejected` or `Yanked` (those come from the host);
- the sender holds the plug, or nobody does and this is a pick-up;
- `PickedUp`: the player is within the grab sphere-cast distance of the plug;
- `DockSocket`: the car exists, is in this station's pit-stop car list, has that socket, the socket
  accepts the plug, and the plug is within 3 m of the socket;
- `DockHome`: the plug is within 3 m of its stand.

The squared distances carry a 10% tolerance (about 3.15 m for docking). Vanilla's `ScanForHit`
uses 2 m plug-to-socket; the host's limit was raised from that to 3 m (commit `a21c479`) to allow
for lag.

A failure sends `Rejected` back to the sender. The hose's rope physics runs on the host only:
`PropHoseRopePatch` transpiles `PropHose.Update` so a stretched hose yanks the plug out of the holder's
hand (`ServerPluggableObject.YankedByRope`, sent as `Yanked`) or snaps it home (`SnappedByRope`, sent
as `DockHome`) on the host's say; elsewhere the rope's unplug, yank, force and snap do nothing.
Remote clients don't snap plugs home on `Awake`/`OnEnable`.

A client reports a plug's drop or dock only after its own player grabbed it (`grabbedLocally`);
any state applied from the network ends that. Vanilla fires `PluggedIn` for every snap, including
the one that replays the host's state when a player comes into a station's range (it finishes
after the bulk update has marked the plugs ready), and reporting those made the host log "attempted
to interact with a plug that they are not holding" and refuse each plug, every time a player
arrived (49 times in one 2026-10-19 session).

Register: every deposit at a service-station register, the host's included, goes through
`CommonCashRegisterWithModulesActionPacket`. `ServerCashRegister.ProcessAction` requires the
player within reach plus 1 m. `AddCash` moves what is still owed (or what the wallet has) from the
wallet into the register and sends `SetFunds`; `Buy` needs units in the basket and enough deposited
cash; `Cancel` cancels. Results go to players in range; rejections (`RejectFunds`,
`RejectedNoItems`, `RejectGeneric`) to the sender, carrying the server's deposited cash, which the
sender keeps (`RejectedNoItems` used to set it to 0 there). The host's own Buy and Cancel run
vanilla and are then sent to every player (`CashRegisterWithModulesPatch`); a Buy only when
vanilla's `Buy` succeeded (a refused one used to empty the clients' registers too). Registers'
`OnEnable`/`OnDisable` run on the host
only, so a client loading or walking away doesn't cancel and refund the basket. Vanilla's
walk-away refund check measures distance to the nearest player
(`CashRegisterBaseReturnMoneyToPlayerCheckPatch`).

`PlayerDistanceGameObjectsDisablerPatch.cs` keeps refill stations and warehouse machines active on
the host while any player is near them, not only the host.

The host player's own station and plug interactions reach the server in-process (`RegisterLocalStations`).
The host's are submitted by its local player session (`LocalPlayerSession.Stations.cs`, which
implements `IStationsClient`). The server validates and relays them like
anyone's, doesn't apply them again (the host's world has them), and sends the host no
rejection. The host's register deposit (`AddCash`) is answered in-process on every path
(`NetworkedCashRegisterWithModules.AnswerLocalRequest`), so its coroutine finishes on the answer
instead of a loopback `RPC_Timeout` of about 0 s.

`pit-stop` (`WHO=client`, `WHO=host`) covers a lever and a plug; nothing covers refuelling a car
or the register yet.

## Junctions and turntables

`Shared/World/JunctionPatch.cs` and `TurntableControllerPatch.cs` add `NetworkedJunction` and
`NetworkedTurntable`. Ids are assigned locally in `Awake` order (`IsIdServerAuthoritative` is
false): `ushort` for junctions, `byte` for turntables. These components, and
`NetworkedGenericSwitch`, are Shared: on a remote client they send through `IWorldClient`
(`WorldClient.Current`, implemented by `NetworkClient.World.cs`); on the server they broadcast
through `IWorldServer` (`WorldServer.Current`, `NetworkServer.World.cs`).

- A junction switched on a client sends `CommonChangeJunctionPacket` (id, branch, switch mode).
- A turntable sends `CommonRotateTurntablePacket` from `OnTick` whenever its `targetYRotation`
  changes, so every tick while someone turns it.
- The server switches the junction or turns the turntable in its own world for another player's
  change, inside `ApplyingRemoteChange` so the server doesn't send it back, then relays it to every
  other player at `ReadyForWorldState` or later (`HandleJunction`, `HandleTurntable`).
- A junction or turntable that moves in the server's own world any other way (the host's player,
  a train, a mod, nobody at all on a dedicated host) is broadcast by the server itself
  (`BroadcastJunctionSwitched`, `BroadcastTurntableRotation`), with no player and no request.
  It used to go through the host's own client as a request, so with no client on the host
  (host-local-session stage 3) it would have reached nobody.
- None of these goes to the host's own player, which shares the server's world. Nothing is checked: not the player,
  distance, or whether a train is on the junction. The last packet wins; a junction or turntable
  the server doesn't have is dropped with a warning.

Before any of that, the client's `ReadyForWorldState` carries a hash of its NetId → name tables for
tracks, junctions and turntables, and the host refuses a client whose hash differs from its own
with a reason ([design/track-graph-check.md](design/track-graph-check.md)).

On join, `ClientboundRailwayStatePacket` carries every junction's selected branch and every
turntable's rotation, by index. The client waits up to 30 s for it
(`RailwayStateTimeoutSeconds`). Junctions or turntables missing locally are logged and skipped
rather than hanging the join.

## Generic switches

Vanilla `GenericSwitch` is a persisted on/off switch. `Shared/World/GenericSwitchPatch.cs` adds
`NetworkedGenericSwitch` once the switch's `persistenceKey` is set; the NetId is the FNV-1a hash of
that key, so both games agree without sending ids.

A client flipping a switch sends `CommonGenericSwitchStatePacket`. The host checks the player can
reach it (`DvExtensions.PlayerCanReach`); if not, it sends the current state back to that player.
Otherwise it sets the switch, which broadcasts the new state to everyone. Switches flipped on the
host broadcast directly. On join, one packet per switch.

## Drilled holes

`ClientboundCustomizationsPacket` sends `WorldCustomization.I.Holes` at `ReadyForWorldState`; the
client clears its holes and adds the host's. Holes drilled after that are not sent. Holes in cars
travel with the train baseline (`NetworkedCarSpawner.SetCustomizations`).

## Explosions

Vanilla runs explosions on every game. Hazmat cargo counts down a random delay on each game
separately, and a loco with fuel blows up wherever its health reaches zero, so explosions happened
at different times on each screen, each pushing and derailing its own copies of the trains.
Engine-failure explosions come from the loco simulation, which runs only on the host, so clients
saw none.

The host decides (`Server/Trains/ExplosionServerPatch.cs`), clients play it
(`Client/Trains/ExplosionClientPatch.cs`), and `Shared/Trains/ExplosionSync.cs` (`ExplosionSync`)
is how both name an explosion. Three kinds (`ExplosionSync.Kind`):

| Kind | Vanilla trigger |
| --- | --- |
| `Cargo` | `CargoReactionBase.ExplodeCargo` |
| `Resource` | `ResourceExplosionBase.ExplodeResource` |
| `Signal` | `ExplosionActivationOnSignal.OnExplosionSignalChanged` reaching 1 (only sent once cars and jobs have loaded) |

- Host: postfixes on all three call `ExplosionServer.Exploded`, which finds the component's index
  among the car's components of that kind and broadcasts `ClientboundExplosionPacket` (car NetId,
  kind, index) to players at `ReadyForTrainSets` or later. The host's physics pushes and derails
  the trains; the result reaches clients through the train sync.
- Client: all three are blocked unless the client is playing the host's explosion. When a client's
  own cargo countdown runs out it clears `aboutToExplode` and does nothing.
  `TrainCarExplosion.ApplyExplosionForceToTrains` never runs on a client.
- `ExplosionPlayback.Play` runs the same component (index clamped): effects, sound, burnt-out
  model and the push on the client's own player. If the car's interior isn't loaded it still
  switches to the exploded model (`TrainCarExplosion.UpdateModelToExploded`).

On join a car's exploded state comes with the train baseline (`TrainsetSpawnPart.Exploded`).

Vanilla's cargo explosion sounds (`CargoEffects.explosionAudio`) are 2D (the near and far layers'
spatial blend curves are flat at 0), so a hazmat car going off anywhere on the map was heard at full
volume by every player. `CargoExplosionAudioPatch` makes them 3D before they play; their distances
were already set for it (the far layer still carries a few km, like the loco explosion prefab's).
Other world sounds were checked in a live game: train, loco and station sounds are already 3D, the
2D ones are ambience, weather and local UI.

Scenario: `explosion` (`DevTools/DvmpScenarios/Scenarios/Explosion.cs`: a client can't set off loco or tank-car explosions; a host
explosion leaves both peers with the same burnt car, derailments and positions).

## Car cargo fires

A car's cargo reaction (`CargoReactionBase`) runs every frame on every game: a leak lit by a lighter
or flare, a burning ground tile, a random roll for self-ignition, being put out. Each game decided
on its own, so a car could burn on one screen only, and a client's burning car never exploded: its
countdown is blocked, and the host's car wasn't burning.

The host decides: `Server/Trains/CargoFireServerPatch.cs` (`CargoFireServer`) sends and lights,
`Client/Trains/CargoFireClientPatch.cs` (`CargoFirePlayback`) holds and forwards, and
`Shared/Trains/CargoFireSync.cs` (`CargoFireSync`) is how both name a fire:

- Host: after each reaction's `Update`, if its state (burning, about to explode) changed since it
  was last sent, it sends `ClientboundCargoFirePacket` (car NetId, the reaction's index among the
  car's cargo reactions, state) to players at `ReadyForTiles` or later. A joining player gets every
  burning car at `ReadyForTiles`.
- Client: the reaction is held to the host's state before and after its own `Update`, so nothing
  local lights it or puts it out. Its fire effects follow the state. The countdown it then runs
  ends at the blocked `ExplodeCargo` until the host's explosion arrives, with the warning sound.
- A client's `TryIgniteExternally` or `TryExplodeExternally` reached from an item's igniter
  (`Igniter.IgniteNow`: a lighter or flare) sends `ServerboundCargoIgnitePacket` instead. The host
  does it on its own car if the player is within 60 m. Other ignitions on a client (an explosion it
  replays lighting the cars around it) are dropped: the host does those itself.

Scenario: `DevTools/Scenarios/car-fire` (a client's radio damage makes a tank leak, its lighter
lights it, and it burns and explodes on both peers; a tank the host lights burns for the client).

## Hazmat ground tiles

Vanilla `HazmatTileManager` keeps an 8 m grid of tiles holding spilled liquids and fire, corrosion,
biohazard and radiation flags, fed by leaking cars and evolved by a cellular automaton. Every game
used to run its own, fed by its own copy of the leaks, so fires and spills were in different places
on each screen and a late joiner saw none.

`Server/World/HazmatServer.cs` (the host), `Client/World/HazmatClient.cs` and
`Client/World/HazmatTileManagerPatch.cs` (clients), and `Shared/World/HazmatSync.cs` (the tile
format):

- Host: `HazmatServer.Tick` runs every 12 ticks (half a second at 24 ticks/s). It compares
  each tile's visible state (the four flags, and each liquid rounded to 20 L) with what it last
  sent, and sends the tiles that changed plus the ones that disappeared as
  `ClientboundHazmatTilesPacket`, up to 256 tiles per packet, in vanilla's own tile format
  (`HazmatGridTile.SerializeData`), to players at `ReadyForTiles` or later.
- Join: at `ReadyForTiles` the host sends every tile (`HazmatServer.SendAll`).
- Client: doesn't simulate. `EvolveLiquidCellularAutomaton`, `PrepareReaction` and `ProcessReaction`
  are skipped, and `UpdateTileLiquidContentExternal` drops the local leak queues (the host's leaks
  feed the host's tiles). `HazmatClient.Apply` replaces each tile's content and spawns or
  removes fire, corrosion and biohazard effects as flags change, as vanilla does when loading a
  save; removed tiles are reset to empty. The radiation effect follows.
- Client ignition: `IgniteTerrainTile` on a client calls `HazmatClient.ForwardIgnite`, which
  sends `ServerboundHazmatIgnitePacket` (tile, strength) if the tile isn't burning and holds
  something, at most once a second per tile. The host ignores tiles it doesn't have or that are more
  than 40 m (horizontally) from the sender, clamps the strength to [0, 100000] and calls
  `IgniteTile`.

Scenario: `DevTools/Scenarios/hazmat` (host spills and ignites, client ignites a second spill, the
client's own spill does nothing; both show the same tiles, burning tiles and roughly the same
liquid).

## Sandbox cargo loader

The comms radio's cargo loader changed only the client's copy of the car; the host's car stayed
empty and the client's cargo was overwritten by the next host update.
`Client/Trains/CommsRadioCargoLoaderPatch.cs` prefixes `LoadPointedCar`/`UnloadPointedCar` on
clients and sends `ServerboundSandboxCargoPacket` (car NetId, cargo NetId, 0 to empty).

`NetworkServer.OnServerboundSandboxCargoPacket` requires the loader to be enabled on the host
(`CommsRadioCheatMode`, or `CommsRadioSandboxCheatMode` in free roam) and the player within 200 m of
the car. It unloads any current cargo, then loads a full car if the cargo type exists and
`IsLoadableOnCarType` for the car. The cargo sync shows the result to everyone.

The radio's damage mode (`Client/Trains/CommsRadioDamagePatch.cs`) was local the same way: a
client damaged or derailed its copy of the car, and the host, which owns car health and the train
physics, never knew (#74). On a client it sends `ServerboundSandboxCarDamagePacket` (car NetId,
damage level 1-10, or derail). The host applies the same checks as the cargo loader, then does what
vanilla does on its own car; the health and train sync show it to everyone.

The other comms radio modes (car spawner, deleter, rerail, crew vehicle) are train requests; see
[design/trains.md](design/trains.md).

Scenario: `DevTools/Scenarios/sandbox-cargo` (client loads and empties a car; both peers agree each
time).

## Host-only world simulation

Some vanilla systems run only on the host and are made to consider every player:

- `Server/Trains/StationLocoSpawnerPatch.cs`: station loco spawners run on the host and spawn when
  any player is within range.
- `PlayerDistanceGameObjectsDisablerPatch.cs`: refill stations and warehouse machines stay active
  near any player (above).
- `Server/World/KeyUsePatch.cs`: `KeyUse.HandleUse` runs on the host only; clients have no request
  path.

## Testing

In-game scenarios are run with dvmp and `DevTools/Scenarios/gate.sh`; see
[testing.md](testing.md#in-game-tests-with-dvmp). World systems with a scenario: `explosion`,
`hazmat`, `shop`, `sandbox-cargo`, and `jobs` (checks the shared wallet after a payout). Time,
weather, sleep, difficulty, junctions, switches and service stations have none.

## Known gaps

- **Broadcasts during scene load are lost.** Players join `peers` at `ReadyForWorldState`, and the
  game data is sent at `ReadyForGameData`. A difficulty change, money change, licence, garage or
  debt-flag change broadcast in between never reaches that client. Money corrects itself on the next
  change; the debt flag and licences stay wrong until something changes them again or the player
  rejoins.
- **Client fast-travel fee.** Nothing forwards a client's fast-travel cost to the host, so the next
  money update hands it back (flagged in `CODE_AUDIT.md`; not checked in game).
- **Fast travel skips aren't validated.** The host relays any amount, including negative ones.
- **Rejected sleep steps the client's clock back.** The client already skipped time locally; the
  weather packet that corrects it is the large backward step the weather sync otherwise avoids.
  The bed menu shows the shared cooldown, so this needs a race or stale state.
- **Weather lags up to 30 s.** The host changing time or weather reaches clients on the next
  periodic update. A client's weather editor changes are local and overwritten.
- **Shop receipts exist only on the presser's game** when a client presses Buy, as before (shops PR 3).
- **Junctions and turntables are relayed, not owned.** No player, distance or occupancy check; two
  players switching within one round trip can end up with different junctions.
- **Service-station interactions are not validated** (`NetworkedPitStopStation.ValidateInteraction`
  returns true), and `OnPlayerDisconnect`/`OnPlayerEnteredCullingRegion` are TODOs, so a player
  leaving mid-interaction isn't cleaned up.
- **Host plug grabs may be swallowed (suspected).** `NetworkedPluggableObject.processingAsHost` is
  set when the host processes a remote plug packet and cleared only in `LateUpdate` when an
  interaction was recorded. If none is, the host's next grab is treated as part of that processing
  and not sent, and its dock is then rejected. From `CODE_AUDIT.md`; not reproduced.
- **Drilled holes are join-only**, and installed gadgets are read at `ReadyForItems` but never sent.
- **Client hazmat cargo ignition.** Only ground-tile ignition is forwarded. A client setting a car's
  hazmat cargo alight has no path to the host.
- **Licence purchase rejections are silent.** The client has already run the register's `Buy`;
  the host logs and sends nothing, and the wallet corrects on the next money update.
- **Clients can't pay fees one at a time or use keys** (`CareerManagerFeesScreenPatch`,
  `KeyUsePatch`). An admin client can pay all the fees at once.
