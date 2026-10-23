# The owned-items list and retrieval

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the code does.
> It covers the career manager's Owned Items page, how the server builds its rows and words where
> each item is, and an owner's retrieval of an item into their Lost and Found. The list and request
> packets are [wire.md](wire.md); the ledger it reads is [ledger.md](ledger.md); who may retrieve is
> [access.md](access.md). Lost and Found itself is R6's `lost-and-found.md`.

| Side | Code |
| --- | --- |
| Core | `PlaceText`, `PlaceFacts`, `RetrievalRule`, `RetrievalKind` (`Multiplayer.Core/Items/OwnedList/PlaceText.cs`) |
| Server | `OwnedItemRows` (`Multiplayer/Server/Items/OwnedList/OwnedItemRows.cs`), `Retrieval` (`Retrieval.cs`); the packets in `NetworkServer.Items.cs` (`SendOwnedList`, `CarryRetrieve`) |
| Client | `OwnedItemsList` (`Multiplayer/Client/Items/OwnedList/OwnedItemsList.cs`), the page `OwnedItemsCareerManagerScreen` (`Multiplayer/Client/Career/`) |

## The page

The career manager's Owned Items page lists every item the player owns, wherever it is, as the
server words it. Opening it asks for the list (`ServerboundItemListRequestPacket`, kind `Owned`);
the answer (`ClientboundItemListPacket`) replaces `OwnedItemsList.Rows`. A row the server marks
`Retrievable` shows "CONFIRM: RETRIEVE"; Confirm sends `ItemAction.Retrieve` with the row's handle.

The list also comes unasked, with request 0, after every retrieval's answer, accepted or refused, so
the page shows where each item really is (OI-LIST-22). It isn't pushed on other changes: the page
asks again when it opens. The labels are English only (OI-LIST-07, paused: list wording).

## The rows

`OwnedItemRows.For(player)` builds the list on the server, from the ledger and the live records:

- one row per **live** ledger entry the player owns (`ItemLedger.LiveOwnedBy`); an ended item (used
  up, destroyed, binned) isn't listed (OI-LIST-02);
- nothing until the ledger has loaded (`ServerLedger.Loaded`): an empty list;
- items in every home: hand, inventory and belt, an offline player's cold rows (their entry's home
  stays `Slot(holder, slot)`), the world, a car, a shelf, a container, Lost and Found, installed.

Each row carries:

| Field | From |
| --- | --- |
| `Handle` | `ItemHandles.Of(entry.Id)`: the session's handle for the item |
| `Prefab` | the entry's prefab |
| `DisplayName` | the stored record's display name for an item in a container, else DV's localised name for the prefab, else the prefab |
| `Where` | `PlaceText.Describe(entry.Home, facts)` |
| `Retrievable` | `Retrieval.CanRetrieve(player, entry)` |

**Handles, not GUIDs.** A row names its item by a `uint` handle; `ItemHandles` (Server
`Items/Projection`) maps it to the item's GUID and back, on the server only. A handle stays the same
for the session, so a retrieval can name a row while the list is open, and a row keeps its handle
when it moves to the Lost and Found list. The row's GUID (`OwnedItemRow.Id`) never leaves the server
(OI-ID-07; [wire.md](wire.md#naming-an-item)).

## Where it is: `PlaceText`

`PlaceText.Describe(home, facts)` words the ledger's home. The server works it out, so every
player's list reads the same. `PlaceFacts` adds the names the ledger doesn't have, which
`OwnedItemRows` finds: the viewer, the holder's name (a connected player with that identity), the
car's id (from the live record's attached car or world parent, `TrainCar.ID`, e.g. L-042), and the
container's name (its record's prefab, localised).

| Home | Text |
| --- | --- |
| `Hand` | "in your hand"; "in NAME's hand"; "in another player's hand" |
| `Slot`, slots 0-32 | "in your inventory"; "in NAME's inventory"; "in another player's inventory" |
| `Slot`, slots 33-35 (the belt) | "on your belt"; "on NAME's belt"; "on another player's belt" |
| `World` | "on the ground" |
| `TrainInterior` | "in L-042", or "in a car" when the car isn't found |
| `Shelf` | "on a shelf" |
| `Container` | "in Toolbox" (the container holding it, not the nesting), or "in a container" |
| `LostAndFound` | "in Lost and Found" |
| `Installed` on a car | "installed on L-042", or "installed on a car" |
| `Installed` on the world (`Home.WorldAnchor`) | "installed in the world" |
| `Attached` | "attached to L-042" |
| `Snapped` | "fitted in place" |
| `Missing` | "missing" (the kind goes in R4, D9) |

"your" is the viewer's own hands and inventory. Another player is named only while connected; an
offline holder reads "another player's". A ground item doesn't say where ("near Harbor, 1.2 km",
OI-LIST-04) and a container doesn't name its nesting ("in your Toolbox, in your inventory",
OI-LIST-05): both are paused (list wording). "attached to L-042" and "missing" are built but no
scenario lists them (OI-LIST-06, R6).

## Retrieval

An owner takes one of their items back from wherever it is, into their own Lost and Found. The owner
asked, so Lost and Found's nearby-player protection and grace period don't apply: the item goes now.

### What each home's retrieval does

`RetrievalRule.For(home, owner)` gives the kind; `Retrieval` carries it out.

| Home | `RetrievalKind` | What happens |
| --- | --- | --- |
| `Slot` or `Hand` of the owner | `NothingToDo` | refused, `nothing-to-retrieve` |
| `Slot` or `Hand` of another player | `TakeFromHolder` | taken from them, then into Lost and Found |
| `World`, `TrainInterior`, `Shelf` | `Collect` | into Lost and Found now |
| `Container` | `OutermostContainer` | the outermost container not with the owner goes, whole |
| `LostAndFound`, `Missing` | `NothingToDo` | refused, `nothing-to-retrieve` |
| `Attached`, `Snapped`, `Installed` | `NotYet` | refused, `not-retrievable-yet` |

Detaching an attached or snapped item first is OI-LIST-14 (R6); retrieving an installed gadget is
OI-LIST-15 (paused: gadgets). A shelf retrieval is built but unproved (OI-LIST-09, R7).

### The outermost container

An item in a container goes as the outermost container that isn't with its owner, everything in it
(OI-LIST-10). `Retrieval.Target` walks up the cold container graph
(`ContainerServer.Graph.ExportSnapshot()`), at most 16 levels, from the item to the first live shell:
a container lying in the world or carried is a live shell, bound by its container id
(`ContainerServer.TryGetShellNetId`); one stored inside another names its shell item, and the walk
goes on from there. If that shell is in the owner's own hand or inventory, there is nothing to do
(`nothing-to-retrieve`). If the walk can't reach a live shell, `unknown-item`. Otherwise the shell is
what goes, from wherever it is, by the rules above. The access check and Lost and Found are asked of
the shell, not the item: a deposit needs only the item to be the depositor's, so an owner's mug in a
toolbox nobody owns, or someone else's, is listed retrievable but refused at step 4 below. Taking the
item itself out of the box, asked about the item, is R5's (OI-LIST-23).

### From another player

An item in another player's hand, inventory or belt is taken from them (OI-LIST-11, -12;
`Retrieval.TakeFromHolder`), as a departing borrower's items are dropped (`PlayerItems.Leave`): a
forced transition drops it where it is (`ItemTransitionReason.HostLocalState`, cause
`ItemMoveReason.TakenFromPlayer`, "NAME took back their ITEM"), the update goes to everyone so it
leaves the holder's hand, and the holder is whispered "NAME took back their ITEM". Then it goes to
the owner's Lost and Found like any other. A cold row is never someone else's item: a leaver's
borrowed items go to their owners' Lost and Found when they leave.

### Into Lost and Found

Every retrieval ends in `ServerLedger.SendToLostAndFound(id, LostItemReason.RetrievedByOwner,
"retrieved by NAME")`, the one entry point by id (OI-LF-08). It refuses an item that is still with a
player (`item-is-with-a-player`), which is why a take from another player drops it first. Lost and
Found records the reason `RetrievedByOwner` (OI-LF-20).

### A data-only subject

An item in a connected remote player's inventory is data only on the server (OI-COLD-06): no object
there. Before a retrieval is carried out, `NetworkServer.DataOnlyNamed` finds its subject
(`Retrieval.Subject`: the item, or for an item in a container the outermost shell) and, if it's data
only, `PlayerItems.WithObject` makes it again under the same NetId first. The retrieval then runs
on the object; `item-data-unavailable` if it couldn't be made.

### The steps and refusals

`NetworkServer.CarryRetrieve` resolves the handle and calls `Retrieval.Retrieve(owner, id)`:

1. the handle and the ledger entry exist, else `unknown-item`;
2. `CanRetrieve`: the entry is live, else `unknown-item`; the requester owns it, else
   `not-your-item` (OI-LIST-20); the rule above allows it, else `nothing-to-retrieve` or
   `not-retrievable-yet`; for a container, the walk finds a shell;
3. the subject (the item, or the outermost shell) has a live record and object, else `unknown-item`;
4. `ItemAccessServer.Allows(owner, Retrieve, subject)`: the subject is the owner's, else
   `requester-not-persistent-owner` ([access.md](access.md));
5. from another player's hand or inventory, `TakeFromHolder`; its transition's refusal if it fails;
6. `SendToLostAndFound`; `item-is-with-a-player`, `item-has-no-owner` or `collection-refused` if it
   doesn't go.

The answer is one `ClientboundItemResultPacket` (`ItemRefusals.Classify` gives the code:
`not-your-item` is `NotYours`, `nothing-to-retrieve` and `unknown-item` `UnknownItem`,
`not-retrievable-yet` `NotAllowed`), followed by a fresh list.

A recall is one step ([wire.md](wire.md#recall)), so a retrieval never meets one half done
(OI-LIST-17). A retrieval of a container mid-withdrawal or mid-deposit isn't refused yet
(OI-LIST-18, R5).

## Tests

Core (`Multiplayer.Core.Tests/Items/OwnedList/PlaceTextTests.cs`):

- `PlaceTextTests`: the owner's own hands, inventory and belt read "your"; another player's name;
  ground, car and shelf; container, Lost and Found, attached, installed on a car and in the world,
  fitted in place, missing.
- `RetrievalRuleTests.EachPlace_HasItsRetrieval`: the kind for each home.

Scenarios (`DevTools/DvmpScenarios/Scenarios/`):

- `owned-list`: each live mug once (inventory, ground, a cab, Lost and Found, a toolbox), not a
  destroyed one, and the client receives the same rows.
- `owned-retrieve`: ground, cab and boxed mugs retrieved into Lost and Found (the toolbox whole), a
  mug in the client's own inventory refused (`nothing-to-retrieve`), a fresh list after the refusal,
  and on a player host the client's lantern taken back out of the host's hand.
- `ledger-rows`: the host's retrieval of the client's mug is refused (`not-your-item`); the owner's
  list names another player's inventory and its own hand (OI-LIST-21); the client retrieves its mug
  from the host's inventory, and Lost and Found records `RetrievedByOwner` (OI-LIST-12, OI-LF-20).
- `item-lineage`: a used-up item leaves the list.
- `rekey`: the owned list and a Lost and Found retrieval survive a re-key.
