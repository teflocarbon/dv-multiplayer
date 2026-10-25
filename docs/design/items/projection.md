# Projection

> Status: built in R7 of the [item refactor](../item-refactor.md) (#389-#398) and merged into
> `items/refactor`, not yet proved in a game: that is the end pass (OI-REF-01).
> It covers interest and what each client is
> shown: the cells, who is sent which item, a client's copy of it, acknowledgements, retries and
> convergence. Scene items, shelves and office stock are [world-items.md](world-items.md); leases and
> settlement are [physics.md](physics.md); the packets are [wire.md](wire.md); why an item went is
> [change-causes.md](change-causes.md). With world-items.md it replaces the legacy
> [item-world-sync.md](../../legacy/items/item-world-sync.md).

| Side | Code |
| --- | --- |
| Core | `Multiplayer.Core/Items/Projection/`: `ProjectionAcks`, `WorldItemIndexing`, `ItemPossessionInterestPolicy`, `RemoteItemProjectionPlanner`, `ItemWireStateProjection`; `KnownItemDeliveryEvaluator` (`Multiplayer.Core/Replication/InterestMembership.cs`) |
| Server | `Multiplayer/Server/Items/Projection/`: `ServerItemManager` (`.WorldIndex`, `.Interest`, `.Convergence`), `WorldItemCellCoord`, `ItemHandles`; the ack and debug handlers in `NetworkServer.Items.cs`; `ServerPlayer.KnownItems`, `NearbyItems`, `AcknowledgedWorldItems`, `ProjectionRetries` |
| Client | `Multiplayer/Client/Items/Projection/`: `ClientItemManager` (`.Projection`, `.Compatibility`; `.Rows`, `.StandIns`, `.TakeOut` are [inventory.md](inventory.md)'s) |
| Shared | `InstalledItems` (`Multiplayer/Shared/Items/LiveState/InstalledItems.cs`) |

## What the server keeps for each player

The server's record ([live-state.md](live-state.md)) is the item. A client's object is a copy of it,
made and taken away by the server. For each player (`ServerPlayer`) the server keeps:

- **nearby** (`NearbyItems`): the items its interest takes in now;
- **known** (`KnownItems`): the items it has been sent a Create or a Lost and Found tombstone for,
  with the tick it was last sent anything. An item is known from the moment its Create goes, before any answer;
- **acknowledged** (`AcknowledgedWorldItems`): the items its game said it has
  ([Acknowledgements](#acknowledgements));
- **a retry hold** (`ProjectionRetries`): when a Create it failed may go again, and how many times in a
  row it has failed.

## Cells

The world is cut into 128 m squares on absolute X and Z (`WorldItemCellCoord`; the size and both
radii are Core's `InterestCells`: `CellSize = 128`, `EnterRadius = 1`, `KeepRadius = 2`). The
server indexes every item it has (`ServerItemManager.RefreshHostWorldItem`):

- **nowhere**: waiting for its restore (`ItemSaveLoad.IsRestorePending`), in Lost and Found,
  destroyed, or an installed gadget's item not yet committed as installed;
- **non-spatial** (`HostNonSpatialItems`): in a hand, an inventory, a container, a magazine, a train
  interior, attached, snapped or installed;
- **by its cell**: in the world or on a shelf (`WorldItemIndexing.IndexedByCell`). A world item its
  owner has a recall claim on is in both.

A shelf item lies where it is, as an item on the ground does. Before R7 a spatial sample or commit
onto a shelf took it out of the cells, nothing else made it relevant, and every client was sent its
retirement 0.3 s after it landed (SH2, F42, OI-SYNC-04).

An item is indexed again when it moves (OI-SYNC-05; `ServerItemManager.WorldIndex.cs`):

- **a committed placement**, in the same operation, at the record's pose: `LiveItems.cs` calls the
  hook `OnAuthoritativeItemTransition`, which calls `RefreshHostWorldItem`. So an item thrown out of
  a car is in its cell before its Unity state applies.
- **a change to one of the server's own copies**: `ProcessChanged` commits it
  (`LiveItems.TryApplyTransition`, or `TryApplyServerTransition` on a dedicated host, through the
  same hook) and then calls `RefreshHostWorldItem` again
  (`ServerItemManager.Interest.cs`).
- **every `RegisterHostWorldItem`** (a load, a restore, a refill, a retrieval and the rest).
- **spatial samples and commits** take a cheaper path, `RefreshHostSpatialPosition`, from
  `ServerItemSpatial`. It does nothing while the item stays in the same cell, or stays non-spatial
  (in a car).

A re-index that puts the item in a cell or among the non-spatial items clears every player's cached
cells (`HostPlayerCells`), so everyone's interest is rebuilt at the next tick (F43). One that takes it
out of the index (waiting for a restore, in Lost and Found, destroyed) doesn't clear them; the next
quarter-second rebuild catches it.

## Interest

Every network tick (24 Hz), `UpdatePlayerItemLists` looks at each player at `ReadyForItems` or later.
Their centre is the cell of `ServerPlayer.AbsoluteWorldPosition`, and their cells are those within a
radius of 1 cell, the 3×3 around it (`EnterRadius`, `WorldItemCellCoord.Neighbourhood`;
`ServerItemManager.Interest.cs`, `UpdatePlayerItemLists`).
Their interest is rebuilt (`RefreshPlayerInterest`) when their cells change, when the index changed,
or a quarter of a second after the last rebuild, for what moves with players (holders, cars).

**What a player is sent.** The items indexed in their 3×3 cells, plus the non-spatial items relevant to
them (`IsNonSpatialRelevantTo`):

| Placement | Relevant to |
| --- | --- |
| installed | everyone: a gadget rides its car, and every player has every car |
| a hand or an inventory | its holder; its owner while they have a recall claim on it; anyone whose cells hold the holder (`ItemPossessionInterestPolicy`) |
| anything with an owner's recall claim | that owner, anywhere: their silhouette and Get button need it |
| a train interior | the car's occupants (`ServerPlayer.CarId`), or anyone whose cells hold where it is in the car now |
| a magazine | whoever knows its tool, or would be sent the tool |
| attached, snapped | the car's occupants, or anyone whose cells hold the car: the coupler's car, or for an item on a gadget's snap point (a hanger), which names only the gadget, the installed gadget's car (`ServerItemManager.CarOf`) |

Only items the player's catalogue takes go in (`ServerPlayer.TakesAuthored`: any item but a scene item
whose key was refused, [world-items.md](world-items.md#the-authored-catalogue)).

**Staying** (OI-SYNC-06). An item already nearby stays until it is outside a radius of 2 cells, the
5×5 around the player (`KeepRadius`; `InterestCells.Wanted`, from `RefreshPlayerInterest`). Without this
hysteresis, walking along a cell edge retired and re-made everything on the other side at each step
(F44).

**Pinned.** Some items are in a player's interest whatever their cells say (`AddPinnedInterest`):

- every item a remote player simulates (a lease, `ServerItemSpatial.SimulatedBy`) stays theirs until
  the lease ends, wherever it flies. These skip the catalogue check (`TakesAuthored`): a simulator
  has the item already, as its releaser or a candidate who acknowledged it (`ServerItemSpatial.Candidates`);
- a train wake that finds nobody with its item pins it, for 10 s from each attempt, only in the
  interest of each player in range who lacks it (and whose catalogue takes it), so their game gets it
  and can take the wake (`ServerItemManager.PinInterest`,
  OI-PHY-12; [train-wake.md](train-wake.md)). A pin brings the player's next rebuild forward. A
  player's pins go when they leave, so whoever takes their number next starts with none.

**Lost and Found.** An item in Lost and Found is never nearby. It is sent once, as a collection
projection (a tombstone), to each remote player who doesn't know it; an essential's owner who has no
copy is sent its Create first, so their silhouette stays ([lost-and-found.md](lost-and-found.md)).

## Leaving interest

An item that leaves a remote player's interest is retired from their game. If they knew it, the
server forgets it (known and acknowledged), and sends a Destroy with `InterestRetirement`. Its cause
is `LeftInterest`, with where the item was (`WhyNotRelevant`: its placement and cell against the
player's, or "in Lost and Found"), and it is published on the server as `world-item.interest-retired`
(OI-SYNC-18). Leaving interest ends the player's right to act on the item: a late answer can't bring
it back.

## Delivery

`ProcessChanged`, every tick:

1. **The server's own copies.** A change to one (`NetworkedItem.GetSnapshot`) is committed as the
   host's own, forced (`HostLocalState`): by the host's player on a player host, by the server itself
   on a dedicated one (`HostItemActor`). For an item another player holds, only the server's own object
   state is kept (`TryKeepRemotePossession`). Items in Lost and Found, waiting for a restore, destroyed
   or installed are skipped.
2. **Per remote player, per nearby item** (`KnownItemDeliveryEvaluator`): unknown gives a Create, known
   at once; dirty gives the dirty update; known since before its last change gives a FullSync. A Create
   is made from the record (`LiveItems.WriteToSnapshot`) and the last accepted spatial pose, a job
   report's render recipe going first. A Create waits while the player's retry hold for it lasts.
3. **Destroys** go only to the players who knew the item (`AddDirtyItemSnapshot`), and the item leaves
   every player's known, acknowledged and nearby sets.
4. Each player's updates go in one send, built for them ([wire.md](wire.md#the-update)), then the
   leases open on what they were sent (`SendActiveLeasesTo`).

A client's accepted update is sent on to every player at `ReadyForItems`, interest or not
([live-state.md](live-state.md#a-change)). Relays that respect interest are paused (OI-SYNC-15).

## On a client

`ClientItemManager.ProcessReceivedAsClient` takes the server's updates in order. Inventory stand-ins
go first ([inventory.md](inventory.md)). A Create is made at once unless its region isn't loaded:

- **a scene item** (it has an authored key) binds this game's own object
  ([world-items.md](world-items.md#authored-projections-on-a-client));
- **a job document** binds the object the job system made, or waits for it
  (`PendingSpecialItemSnapshots`);
- **anything else** is a fresh prefab instance (`CreateItem`), for that Create's lifetime only. An
  object already holding that NetId is retired first.

Each one acknowledges `Projected`. One it can't make (no prefab, a scene key it lacks) acknowledges
`Failed` (`RefuseProjection`). Before R7 this immediate path sent nothing, and the server believed the
client had the item (OI-SYNC-07).

**Deferred.** A Create for an item dropped, thrown or attached whose region isn't ready waits
(`PendingWorldProjections`). Ready means its car exists, for an attached or train-interior item, or
else `WorldStreamingInit.IsSceneAndTerrainRegionLoaded` at its position. Later updates for it queue
(the last 64). A Destroy cancels it and acknowledges `Retired`. Up to 8 ready ones are made a frame:
each is made without an ack, its queued updates are applied, and then one `Projected` goes with the
last revision applied. One that can't be made acknowledges `Failed`.

**Retired.** A Destroy takes the copy away (`RetireClientProjection`): unbound, out of vanilla's
storage and inventory lists, inactive, NetId 0. A scene item goes dormant and keeps its key; a dynamic
one is destroyed. A job document retired by interest goes dormant instead; a Lost and Found collection
leaves a tombstone; an installed item is kept ([below](#installed-items)). An `InterestRetirement` is
acknowledged `Retired`.

## Acknowledgements

A projection is known only once acknowledged (OI-SYNC-07). An ack (`ServerboundWorldItemProjectionAckPacket`)
names the item, the revision the client applied and its kind (`ProjectionAck`, Core `ProjectionAcks`):

| Kind | Says | Outcome on the server (`ProjectionAcks.Apply`) |
| --- | --- | --- |
| `Projected` | it has the item, at that revision | `Acknowledged` if the item exists, the player knows it and the revision is no later than the record's; else `Rejected` |
| `Retired` | it took the item out, as a retirement said | `Unacknowledged` |
| `Failed` | it was sent a Create it couldn't make | `Forgotten` if the item exists and the player knows it; else `Unacknowledged` |

`NetworkServer.OnServerboundWorldItemProjectionAckPacket` carries it out:

- **Acknowledged:** the item joins the player's acknowledged set, and its retry hold goes.
- **Rejected:** logged (`world-item.projection-ack-rejected`); nothing changes.
- **Unacknowledged:** the item leaves the acknowledged set; what the server knows it sent stays.
- **Forgotten:** it leaves the acknowledged and known sets, and a retry hold is set ([Retries](#retries)).

An older revision than the record's still counts: other players' updates can move the item on while
the ack is in flight, and those follow it on the same reliable ordered channel. Requiring an exact
match left new job overviews unacknowledged and their pickups refused (`overview-pickup`).

Before R7 an ack was a bool, so a retirement's and a failure's looked alike. A retirement's ack that
arrived after the item came back into interest made the server forget the new Create, hold it back
5 s and send it again over the copy the client had made (F45). Now only a failure forgets.

**What it allows.** The acknowledged set is the server's word that a player's game has the item:

- a player who doesn't hold an item may act on it only once it is acknowledged (a pickup, a book's
  page, working it where it lies), else `item-not-projected-to-sender` (`NetworkedItemServer`); a
  pickup must also be within reach ([physics.md](physics.md));
- only a player who has the item acknowledged may simulate it (`ServerItemSpatial.Candidates`).

A player whose release the server accepts has the item in their world, whether or not they were sent
it there, and is added to the set. The item leaves it whenever it leaves their game: a retirement, a
destroy, a deposit, a stash to data or a cold inventory, Lost and Found.

### Retries

A `Forgotten` Create is sent again once it is relevant and its hold is over. A failure can pass (a
scene a frame late) or never pass (a modded item's prefab the client lacks, an authored key its scenes
don't have), so each failure in a row doubles the hold: 5, 10, 20 and 40 s, and after those four
retries no more while the item stays in the player's interest (Core `ProjectionRetry`,
`ProjectionRetry.DelayAfter`). The count goes when the player acknowledges the item, or at the next
interest rebuild after the item left their interest, so it starts afresh when it comes back. Until
then the player is sent no updates for an object it doesn't have. Before the R7 fixes the hold was
always 5 s and cleared when the Create went, so a Create that could never succeed went every 5 s for
as long as the player stayed near it, and the client logged a missing prefab each time.

## Convergence

Replication is by event: once a client knows an item, it hears of it again only when it changes. Some
vanilla code moves things without the mod noticing, so the server re-sends a few items
(`ServerItemManager.Convergence`). Every 0.1 s, each remote player at `Complete` is sent the next item
they know, in turn, that is in the world, a train interior or on a shelf, with no open lease, not in
Lost and Found and not waiting for a restore. It goes as a `Dropped` update with the record's revision
and the server's live pose, and `TransitionReason.Convergence`.

The client acts on it only if its copy disagrees (`NetworkedItemClient.IgnoresConvergence`): not while
it is grabbed; a newer revision; the copy not lying in the world; more than 0.25 m off, in the car's
frame for a train item. One it acts on is logged as `HostCorrection` with how it disagreed (OI-SYNC-18).

## Installed items

An installed gadget's item is every player's to know, and the item code never destroys it, on any game
(OI-GAD-06, F49). Only the gadget code takes it out of `Installed`, first, when it goes
([gadgets.md](../gadgets.md#the-installed-gadget-and-its-item)). `ProcessChanged` skips it: it changes
only with its gadget.

Every item-code destroy asks `InstalledItems.RefuseDestroy` first. It refuses, logs a warning and
publishes `item.destroy-refused`. The server goes by its record (`IItemManagerServerPart.IsInstalled`),
a client by the last state it was sent (`NetworkedItem.LastObservedState`). The server asks where a
destroy doesn't need possession first: remaking an item from an unloadable state, a leaver's or a
stashed item going cold or to data, Lost and Found data, a magazine load, a use-up, an abandoned stock
fork, and an authored slot's old copy. A client asks as it retires a copy and at the server's Destroy.

## The recipients debug state

The server's item debug state (`IItemServer.AuthorityDebugState`) lists, under `recipients`, what each
player has of the item (`ProjectionDebugState`, OI-SYNC-17): `playerId`, `self` (the host's own player),
`known` and `knownTick`, `acknowledged`, `retryInSeconds` (-1: no more Creates while it stays in
interest), `projectionFailures`, `pinnedForSeconds` and `catalogueRefuses`.

## Player-hosted and dedicated servers

Projection runs the same on both, but for who the host's world is:

- **On a player host** the host's own player (`HostItemActor.Player`, on the server's `SelfPeer`) plays
  in the server's own world. Its interest is worked out, but it is sent no Creates, Destroys,
  retirements, tombstones or convergence, and isn't pinned what it simulates. Its game's changes are
  the server's own copies' changes, committed in its name.
  It never sends a world-item catalogue (only a client's `NetworkClient.SyncWorldState` does), so
  `WorldItemCatalogueAccepted` stays false for it and `TakesAuthored` refuses it every scene item:
  its `NearbyItems` leaves scene items out (`RefreshPlayerInterest`, and a wake's pins in
  `AddPinnedInterest`). That is harmless. Its game is the server's world and has every object, and
  what reads its nearby set skips its peer anyway: `ProcessChanged` (`ServerItemManager.Interest.cs`),
  retirements and tombstones (`RefreshPlayerInterest`, `UpdatePlayerItemLists`) and convergence
  (`ServerItemManager.Convergence.cs`). It is a simulator candidate by its object being active, not by
  interest (`ServerItemSpatial.Candidates`). The one other reader is office stock, where it makes no
  difference ([world-items.md](world-items.md#office-stock)).
- **On a dedicated host** there is no player of the host's (`HostItemActor.IsServer`): every player is
  a remote client, and the server commits its world's changes itself, with an empty actor
  (`LiveItems.TryApplyServerTransition`). The server can simulate an item itself
  ([physics.md](physics.md)).

## Paused and not on this page

- Paused (D4): a sync-completion barrier at join (OI-SYNC-14), relays that respect interest
  (OI-SYNC-15), compressed or chunked bulk updates (OI-SYNC-16).
- Leases, samples, settlement and pickup reach: [physics.md](physics.md). Items in moving trains:
  [train-wake.md](train-wake.md). Stand-ins and rows: [inventory.md](inventory.md).

## Tests

- `Multiplayer.Core.Tests/Items/Projection/`: `ProjectionAcksTests` (each kind and outcome; the retry holds),
  `WorldItemIndexingTests` (a shelf item by its cell), `InterestCellsTests` (128 m cells, in within
  one cell, kept to two, OI-SYNC-06), `ItemPossessionInterestPolicyTests`,
  `RemoteItemProjectionPlannerTests`, `ItemWireStateProjectionTests`.
- `Multiplayer.Core.Tests/Replication/InterestMembershipTests.cs`: dirty against full sync, known-item
  delivery.
- `Multiplayer.Protocol.Tests/WorldItemAuthorityPacketTests.cs`: the ack and catalogue packets.

## Scenarios

In [testing.md](../../testing.md) and `DevTools/Scenarios/gate.sh`. They run at the end pass (OI-REF-01),
on both host kinds unless marked:

- `projection-acks`: `failed` (a failed Create is re-sent and acknowledged), `MODE=failing` (a Create
  that always fails is retried four times, then not until the item comes back into interest),
  `MODE=late-retire` (F45), `MODE=cause` (the recipients state; `HostCorrection` and `LeftInterest`),
  `MODE=collision` (a second object of one scene item: that key refused alone, [world-items.md](world-items.md));
- `item-bug-shelf-unseen` (SH2, player host only), `item-bug-client-shelf`, `shelf-item` (a late joiner
  sees a shelf item);
- `installed-kept` (OI-GAD-06);
- `container-interest` (a deposit's Destroy only to who knew it; player host only; in testing.md, not
  in gate.sh's list), `server-throw` (an
  item indexed in its cell at once; player host only), `late-join-train-item` (`MODE=convergence`
  too), `overview-pickup` (an older ack revision counts);
- `rejoin` and `starting-items`, which re-prove OI-SYNC-01.
