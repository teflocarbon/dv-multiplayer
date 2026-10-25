# The item wire

> Status: built in R2 of the [item refactor](../item-refactor.md#the-wire), 2026-10-24. This page
> says what the code does. Six packets carry every item change, request and list, all in
> `Multiplayer/Shared/Protocol/Packets/Items/`, with their data in `Shared/Protocol/Data/Items/`
> (`ItemWirePacketTests.ThePacketsOfTheWireAreTheItemsNamespace`); the sixth, the inventory rows,
> came with rows on every game (D14) in R4C. The spatial packets (lease, sample, settlement, commit),
> the wake witness, the projection acknowledgement and the authored catalogue are the
> [physics](physics.md) and [projection](projection.md) docs' to describe. Two of them have
> changed since R2: the spatial commit carries its move's cause (`MoveReason`, `MovedByPlayerId`,
> `MoveDetail`), and the acknowledgement says what the client did (`Kind`: `Projected`, `Retired`
> or `Failed`).

| Packet | Way | What |
| --- | --- | --- |
| `ClientboundItemUpdatesPacket` | to a client | a list of `ItemUpdate`, built for that client |
| `ServerboundItemRequestPacket` | to the server | one `ItemRequest`: a player asks for something to happen to an item |
| `ClientboundItemResultPacket` | to a client | the one answer to a request |
| `ServerboundItemListRequestPacket` | to the server | asks for one list |
| `ClientboundItemListPacket` | to a client | a list: Lost and Found, owned items, or a container's slots, as `ItemRow`s |
| `ClientboundInventoryRowsPacket` | to a client | rows of the recipient's own inventory: each item's `Create`, built for them, and its saved state beside it (`Full`, `Rows`, `States`); the whole inventory at the join (`Full`), and each item the server puts in it itself ([inventory.md](inventory.md)) |

Two more carry a stashed boombox's music and a stashed alarm clock's ring, which are presentation,
not a change, so they aren't in `Items/`: `Serverbound/ServerboundStashedCassettePacket` (its holder's
game: where the cassette has got to, for its row) and `Clientbound/World/ClientboundStashedSoundPacket`
(to each player near the holder: what it plays, that it stopped, or, with `Once`, an alarm's ring or
turn). [inventory.md](inventory.md#stashed-items-that-act)
says what they do.

A player host's own player sends its requests and asks for its lists with the same packets, through
its local session (`LocalPlayerSession.Items.cs`), handled in-process and answered with the same
result and list packets. Its own grabs, drops, throws and leases are the server's world already and
go to the server's part directly (`NetworkedItem.ServerPart`), and it is never sent updates or
inventory rows (`NetworkServer.SendItemUpdatesTo`, `SendInventoryRows`).

## Naming an item

- **A NetId** (`ushort`) for an item with a live object.
- **A handle** (`uint`, `ItemHandle` in a request, `Handle` in a row) for anything a client sees
  only as a row: a Lost and Found entry, an owned-items row, a container slot, a container itself.
  There is one handle space for the session: `ItemHandles` (Server `Items/Projection`) maps an item's
  or a container's GUID to a handle, and back, on the server only. A handle stays the same for the
  session, so a row keeps its handle when it moves between lists.
- **Never a GUID.** No item packet, nor anything inside one, has a `Guid` field
  (`ItemWirePacketTests.NoItemPacketCarriesAGuid` walks the types of every packet with `Item` in its
  name, and every packet of the `Items` namespace, `ClientboundInventoryRowsPacket` among them; OI-ID-07).

## Naming a player

- **A holder** by session number (`ItemUpdate.PlayerId`): the one player number an update keeps,
  because clients know players only by it. The cause's actor (`OriginatingPlayerId`, and the
  cause's own number and name) stays a session number too, for display and so a game knows its own
  change coming back.
- **An owner never.** Each player is told its **relation** to the item, `ItemUpdate.Relation`
  (`ItemRelation`, Core `Items/Ledger`): `Mine`, `SomeoneElses` or `Nobodys`, worked out by
  `ItemRelations.Of(owner, viewer)`. A found item that nobody owns is `Nobodys`: finding isn't owning.
- **The claim only to its claimant.** An inventory claim (`InventoryClaimSlot`, `InventoryClaimFlags`)
  is sent only to the owner, or, for an item nobody owns, to its holder; every other recipient gets
  slot -1 and no flags, and the claim isn't written at all (`ItemUpdate.For`, `HasClaim`). So a
  client never sees another owner's claim and can't restore it onto its own slot (OI-ESS-10).
- **Red for every item someone else owns** (D3, OI-ESS-12): the inventory shows an item red when its
  relation is `SomeoneElses`, whether the owner is here or not
  (`InventoryIntegration.Project(...).ShowForeignStyle`). A list row says the same with
  `ItemRowFlags.SomeoneElses`.

On the server's own copy of an item (`NetworkedItem.Relation`), the relation is the host's player's;
on a dedicated host, which has no player, every owned item is `SomeoneElses`.

## The update

`ClientboundItemUpdatesPacket { List<ItemUpdate> Updates }` is the only item update packet. The
server builds each update **per recipient** (`NetworkServer.SendItemUpdatesTo` calls `BuildFor` for
each one: the relation, and the claim if the recipient is the claimant) and sends it by interest.
An `ItemUpdate` has:

- on every update, a header (`ItemUpdate.Serialize`): the type (`Create`, `Destroy`, `ItemState`,
  `ObjectState`, or `FullSync`, which is both states), the NetId, the revision (`AuthorityRevision`),
  the relation, whether there is a claim and, if there is, its slot and flags, the transition reason
  (`TransitionReason`) and the cause's actor (`OriginatingPlayerId`);
- on a `Destroy`, nothing more but why it went away (`RemovalCause`), when the server knows (into a
  container, into Lost and Found, used up), and not for a recipient the item merely left the
  interest of;
- with item state (or `Create`), why it moved (`MoveCause`);
- on every other update, its home (`WireHome`), and on `Create` the prefab, the scene-authored key
  and vanilla's `BelongsToPlayer`;
- with item state (or `Create`), the home's own fields, a union (OI-SYNC-21): `World` the pose;
  `TrainInterior` the pose, the car's NetId and the pose in the car; `Shelf` the pose, the shelf's key
  and the pose on it (these three also whether it was thrown, and then the throw direction); `Hand`
  the holder and the hand; `Slot` the holder and the slot; `Attached` the car's NetId and which end;
  `Snapped` the gadget item's NetId, never its key, which stays on the server ([live-state.md](live-state.md#attached-and-snapped-items)); `Installed` the car's NetId (0 on the world); `Magazine` the tool's
  NetId and the slot (`MagazineNetId`, `MagazineSlot`);
- on `Create` or with object state: the item type's own values (`States`).

The transition reason `ClientAdoption` and the move reasons `ReturnedToRejoiningOwner` and `Adopted`
are gone since R2 (a4b0faad, 2026-10-24): nothing makes or reads them.

A home is one of the live placements (`ItemPlacementKind`). `Container` is never a live placement,
only a save row's: a stored item has no record (`ItemSaveLoad.Save.cs`). `SnappedAttachment` is an
item on a gadget's snap point (a hanger, a mount), live or loaded: it goes out as `Snapped`, the gadget
by its item's NetId (`ItemWorldParentKind.SnapPoint`), and its ledger home is `Snapped(key)`. An item
on a car's end is `Attached` with the car's NetId (`LiveItems.PlacementFrom`).


A player's own change never goes up as an update: it's a request.

## One request, one result

`ServerboundItemRequestPacket { ItemRequest Request }`. An `ItemRequest` has a `RequestId` (per
client, counting up from 1, given on its first send: `ItemExchange.Number`), the item (a NetId, or a
handle for a row), the revision the requester last saw, and an action with its arguments. Only the
arguments the action uses are written:

| Action | Its arguments | Carried out by |
| --- | --- | --- |
| `Take` | the hand; the item's states | the live state (`CarryChange`) |
| `Stash` | the slot and claim flags; the item's saved state as the requester's game put it away (`SavedState`); states | the live state |
| `MoveSlot` | the slot | moves the item to that slot of its holder's inventory, or swaps it with the holder's own item there (`NetworkServer.CarryMoveSlot`, `LiveItems.TrySwapSlots`); refused on `slot-invalid`, `slot-occupied` or `slot-reserved` (R3, [inventory.md](inventory.md)) |
| `Drop` | the pose and its parent (world, a train car, a static parent); states | the live state |
| `Throw` | as `Drop`, with a direction | the live state |
| `Recall` | the claim slot | `LiveItems.TryRecall`, or Lost and Found for an essential there |
| `Retrieve` | none (the handle names the owned-items row) | owned items: into the owner's Lost and Found |
| `CollectLost` | the slot | Lost and Found |
| `Deposit` | the container's handle, or its shell's NetId; the slot | containers |
| `Withdraw` | the slot | containers |
| `MoveInContainer` | the container, its revision, the slot | containers |
| `DepositMoney` | none | the wallet; refused on `money-not-spendable` |
| `ReportBelowMap` | the surface height | the terrain guard |
| `Attach` | the car's NetId and which end; states | the live state |
| `SetState` | the states, and whether it was a world interaction (a lever, a switch) | the live state |
| `LoadMagazine` | the tool's NetId and the magazine slot; states | the live state; refused on `magazine-tool-unknown`, `magazine-tool-foreign` (a tool another player holds), `magazine-slot-invalid`, `magazine-refused-item`, `magazine-slot-taken` ([containers.md](containers.md#ammo-in-a-magazine)) |
| `UseUp` | the tool it is used in (`MagazineNetId`: a spray can's sprayer; 0 for a key) | `UseUp.Carry` ([vanilla-storage.md](vanilla-storage.md#used-up)); refused on `item-unknown` or `tool-unknown` (`UnknownItem`), else `NotAllowed`: `item-installed`, `key-not-in-hand`, `no-padlock-near`, `sprayer-not-in-hand`, `can-not-in-sprayer`, `not-a-consumable` |

`ItemAction` is a byte, append-only. `CollectLost` names only the slot asked for: the server plans
the collection's slot from its own rows (`LostAndFoundServer.TryCollect`, `LostItemRetrievalPlanner.Plan`
with `ServerLedger.TakenSlots`), as every slot is the server's (R3; before R8 the client sent its
inventory's size and occupied slots).

`ClientboundItemResultPacket { RequestId, Refusal, Detail, Revision, ItemNetId }` answers each
request once: accepted (`Refusal == None`) or a refusal code and its detail, the revision the
request made, and the live item it ended with (a withdrawn or collected item's new object). What
changed arrives as updates and lists, never in the result.

**A repeated `RequestId` does nothing twice** (F21, OI-CON-07). Each player's server-side session
remembers its last 256 requests (`ItemRequestMemory`, Core `Items/LiveState`; `ServerPlayer.ItemRequests`):

| A request id that is | The server |
| --- | --- |
| new | carries it out and remembers its result |
| still being carried out (a withdrawal, or any request waiting for an item kept as data to be made again: `PlayerItems.WithObject`) | does nothing: the one answer is on its way |
| already answered | sends the remembered answer again, and does nothing else |
| older than every id it still remembers | refuses it, `Expired`: it may have run already |

Request 0 is never remembered. The `item-request-repeat` scenario sends one withdrawal twice in a
frame and once after its answer: one mug comes out, and the late repeat gets the same answer.

### Refusals

`ItemRefusal` (Core `Items/LiveState/ItemRequests.cs`), a byte, append-only. The detail stays the
precise reason the server's code gives (`deposit-item-not-owned`, `inventory-full`, ...);
`ItemRefusals.Classify(detail)` gives the code a client can act on.

| Code | Means |
| --- | --- |
| `None` | accepted |
| `Refused` | refused for a reason only the detail names |
| `UnknownItem` | the item, row or container isn't known (any more) |
| `StaleRevision` | it changed since the revision the request expected |
| `NotYours` | it's someone else's, or the requester may not do this to it |
| `Busy` | something else is happening to it: try again |
| `Invalid` | the request makes no sense (bad arguments, an unknown action) |
| `NoRoom` | there's no room where it would go |
| `NotHere` | the requester can't reach it or hasn't been shown it |
| `NotAllowed` | that kind of item can't do this |
| `Expired` | the request id is older than any the server remembers |

## Recall

A recall is one step, like a retrieval from a hand: the owner's game sends `Recall` with the claim
slot, the server commits it, and the holder's game is sent the update that empties their hand (the
item's new placement, in the owner's inventory). There is no prepare / prepared exchange and no
timeout, so a recall is never half done, and nothing else meets one mid-way: a retrieval has no
"busy, mid-recall" case any more (OI-LIST-17).

## One list

`ServerboundItemListRequestPacket { RequestId, Kind, ShellNetId, Container }` asks for one list:
`LostAndFound`, `Owned`, or a `Container`, named by its shell's NetId (a toolbox on the ground) or by
a handle (a container row, or a list's own).

`ClientboundItemListPacket { RequestId, Kind, Refusal, Detail, Container, Revision, Capacity, Rows }`
answers it, with `ItemRow` structs, not parallel arrays: the handle, prefab, display name, where it
is or why it's there (worded by the server), flags (`Retrievable`, `SomeoneElses`, `Container`), the
slot (in a container), the revision a request about it expects, and for a lost essential its claim
slot and the NetId the server keeps it by (`ClaimSlot`, `ItemNetId`): its owner's game shows its
silhouette from them ([essentials.md](essentials.md#the-silhouette)). A
container list also has its own handle, revision and slot count, and can be refused (a container
that can't be opened says why); the other lists never are.

A list also comes unasked, with request 0: the recipient's Lost and Found at the join and whenever it changes, and
its owned items with every retrieval's answer, accepted or refused, so the page shows where each item
really is (OI-LIST-22).

## Proof

- `Multiplayer.Protocol.Tests/Items/ItemWirePacketTests.cs`: no item packet carries a GUID; the six
  packets are the `Items` namespace; every packet round-trips (the inventory rows with their states
  and `Full`, `InventoryRows_RoundTrip...`); a request for each action writes only its arguments; an
  update tells each relation and only the claimant the claim (a claimless update is smaller);
  translation of a player's own change into a request, through the wire; a debug projector for each of
  the six (`ProjectorsCoverEveryPacketOfTheWire`; the inventory rows' since R8).
- `Multiplayer.Core.Tests/Items/LiveState/ItemRequestsTests.cs`: the request memory (new, pending,
  repeat, expired, request 0), refusal codes, relations.
- Scenarios ([testing.md](../../testing.md)): `red`, `item-request-repeat`, `recall` (both `TAKEN`
  modes), `owned-retrieve` (the fresh list after a refusal).
