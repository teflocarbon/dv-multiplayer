# Item access

> Status: built in R3 of the [item refactor](../item-refactor.md), 2026-10-24. This page says what the code does.
> It covers `ItemAccess`, the one place that decides what a player may do with an item, the
> server's side that asks it (`ItemAccessServer`), and fixtures. What each action then does is the
> owning subsystem's: [inventory.md](inventory.md), [essentials.md](essentials.md) (recall),
> [owned-list.md](owned-list.md) (retrieval); containers are [containers.md](containers.md).

| Side | Code |
| --- | --- |
| Core | `ItemAccess`, `ItemAccessAction`, `ItemAccessRequest`, `ItemAccessDecision`, `ItemFixtureList` (`Multiplayer.Core/Items/Access/ItemAccess.cs`) |
| Server | `ItemAccessServer` (`Multiplayer/Server/Items/Access/ItemAccessServer.cs`) |

## The one place that decides

`ItemAccess.Decide(ItemAccessRequest)` returns an `ItemAccessDecision`: allowed, or refused with a
reason string that becomes the request's refusal detail. It is pure Core, with no game references,
and weighs only the facts it is given:

| Fact | Meaning |
| --- | --- |
| `Action` | what the player asks to do (`ItemAccessAction`, a byte, append-only) |
| `ActorOwnsItem` | the item is the actor's own |
| `IsFixture` | the item is a station fixture: part of a place, never a player's |
| `IsEssential` | the item is one of DV's essentials |

No client decides: the server asks, and a client only shows the answer. The default policy is the
rules the server already had before `ItemAccess` existed (OI-ACC-01).

## The actions and their rules

`Decide` checks, in order: a fixture, then an essential's deposit, then the action's own rule.

| Action | Default rule | Refusal | Asked |
| --- | --- | --- | --- |
| `Stash` (into an inventory slot; the VR belt is slots 33-35, no separate action) | anyone, but not a fixture | `fixture-stays-in-its-place` | yes (OI-ACC-02) |
| `Deposit` (into a cold container) | the actor's own item only; never a fixture; never an essential | `deposit-item-not-owned`, `fixture-stays-in-its-place`, `essential-not-in-containers` | yes (OI-ACC-03) |
| `Withdraw` (out of a cold container, into the slot the server picks) | anyone the box lets in (its keeper, [containers.md](containers.md#who-keeps-a-box)) | none | yes (OI-ACC-04) |
| `TakeFromWorld` (from the world, a train, a shelf, a car's end, a snap point) | anyone, fixtures too: a fixture is carried in the hand | none | yes (OI-ACC-07) |
| `TakeFromPlayer` (out of another player's hand or inventory) | nobody | `take-from-player` | yes (OI-ACC-07) |
| `RemoveGadget` (an installed gadget off a car) | anyone | none | no: OI-ACC-06 and OI-GAD-07, paused (gadgets) |
| `Retrieve` (into the owner's Lost and Found, from the owned-items list) | the owner only | `requester-not-persistent-owner` | yes (OI-LIST-20) |
| `Recall` (an essential into its claim slot) | the owner only | `requester-not-persistent-owner` | yes (OI-ACC-05) |

Vanilla has no way to take an item from another player, and an owner's way back is retrieval or
recall, so `TakeFromPlayer` is always refused.

On the wire a refusal is the detail plus a code ([wire.md](wire.md#refusals)). A deposit's refusal
is the container's `Unauthorized`, sent as `NotYours`, whatever the reason. The others are classified
from the detail (`ItemRefusals.Classify`): `requester-not-persistent-owner` is `NotYours`,
`fixture-stays-in-its-place` `NotAllowed`, and `take-from-player` `NotYours`.

## The facts, on the server

`ItemAccessServer.Allows(actor, action, item, record, out reason)` works out the facts for a request
on one item and asks the policy:

- **Owns it:** the record's owner is the actor's identity, `record.Owner == actor.Guid`, and not
  empty (OI-ACC-08). `LiveItem.Owner` is read from the ledger (`ServerLedger.OwnerOf`), so the
  ledger decides who owns it. An unowned item is nobody's, so it is never "the actor's own": anyone
  may stash or take one, nobody may deposit one.
- **Is a fixture:** `ItemAccessServer.IsFixture`, from the record's prefab and authored key (or the
  item's own, when the record has none) against `ItemAccessServer.Fixtures`.
- **Is an essential:** the item's `InventoryItemSpec.IsEssential`.

A container's item is a row with no object, so a withdrawal asks with the row's facts instead
(`ItemAccessServer.AllowsRow`): its owner from the ledger, its prefab and authored key, and its
prefab's spec.

## Fixtures

A fixture is a scene item that belongs to a place, such as a shop's scanner. `ItemFixtureList`
matches one by prefab name or by authored key. Its rules in `ItemAccess`: a fixture can't be stashed
or deposited, even by an owner, but can be carried in the hand (OI-FIX-01). The server's list,
`ItemAccessServer.Fixtures`, is **empty**: filling it with the shops' scanners is OI-FIX-02, and the
rest of the fixture design (put back when carried away, on leaving and at every save; never owned;
the client refusing it too) is OI-FIX-03 to -06, all paused with shops. Scenarios mark a prefab as a
fixture in memory for a run (`DevTools/DvmpScenarios/Library/OwnedItems.cs`).

## Essentials stay out of containers

`Decide` refuses an essential's `Deposit` with `essential-not-in-containers`, before the ownership
rule, so even its owner can't deposit it (OI-ESS-07). The item stays in the inventory, where its
claim slot and recall keep working ([essentials.md](essentials.md#kept-out-of-containers)). A stash
of an essential is allowed as usual.

## Where the server asks

| Call site | Action | When |
| --- | --- | --- |
| `LiveItems.TryApplyTransition` (`Multiplayer/Server/Items/LiveState/LiveItems.cs`) | `TakeFromWorld` or `TakeFromPlayer` (`LiveItems.TakeAction`) | a transition with an actor that places the item in a player's hand or inventory, from the world, a car, a shelf, a car's end or a snap point (`TakeFromWorld`), or from another holder's hand or inventory (`TakeFromPlayer`); asked before the state machine |
| `LiveItems.TryApplyTransition` | `Stash` | after the state machine, when the item enters a player's inventory from anywhere else, whoever commits it: the actor, or with no actor the holder's session (a dedicated host's own copy of a client's item reports the client's stash with no actor) |
| `ContainerServer.Deposit` (`Multiplayer/Server/Items/Containers/ContainerServer.cs`) | `Deposit` | once the depositor is known to have the item |
| `LiveItems.TryRecall` | `Recall` | after the essential check (`item-not-recallable`), before the state machine |
| `LostAndFoundServer.TryRetrieve` (`Multiplayer/Server/Items/LostAndFound/LostAndFoundServer.cs`) | `Recall` | a collection or a recall from Lost and Found, after `LostItemRetrievalPlanner` (the entry's owner), before the move |
| `Retrieval.Retrieve` (`Multiplayer/Server/Items/OwnedList/Retrieval.cs`) | `Retrieve` | on the retrieval's subject, the item or its outermost container's shell, after `CanRetrieve` (asking about the item itself is R5's, OI-LIST-23) |

Not asked: a move inside the player's own inventory (`MoveSlot`, a swap: the item is already
theirs to hold), a withdrawal (OI-ACC-04, R5), a gadget's install or
removal (OI-ACC-06, paused), and the server's own moves that don't put an item with a player (a
load, a collection into Lost and Found, a drop). A server move that does, with a player as its actor,
is asked like a player's: a leaver's own hand item stashed into a free slot (`PlayerItems.Leave`)
is a `Stash`.

Two checks come before `ItemAccess` and usually answer first with their own string: a retrieval of
someone else's item stops at `CanRetrieve` with `not-your-item` ([owned-list.md](owned-list.md)), and
the ledger's slot rules (`slot-reserved`, `slot-occupied`) refuse a stash whose slot is taken, after
the access check has allowed it.

## The take check comes before the state machine

`ItemStateMachine.Apply` refuses a placement into the hands of a player who isn't the item's current
holder with `sender-not-current-possessor`. If `ItemAccess` were asked after it, as the stash check
is, a take from another player would never reach it. So `TryApplyTransition` works out
`TakeAction(record, actor)` and asks first: a take from another player is refused with
`take-from-player`, the refusal is published (`item.transition-rejected`), and nothing changes
(OI-ACC-07). The check runs for forced transitions too, whenever there is an actor. An item in a
remote player's inventory is data only on the server; a request that names it has it made again
first (`PlayerItems.WithObject`), so the check sees its record either way.

## The coming permissions system

Friends, company members and other permissions will replace the default policy inside
`ItemAccess.Decide` (and the facts `ItemAccessServer` gathers), not the call sites: every place that
asks today keeps asking. That work is OI-ACC-09, paused with shops.

## Tests

Core (`Multiplayer.Core.Tests/Items/Access/ItemAccessTests.cs`):

- `TodaysRules_LetAnyoneDoTheEverydayThings`: stash, take from the world, withdraw, remove a gadget.
- `Depositing_NeedsTheItemToBeTheActorsOwn`, `RetrievingAndRecalling_AreTheOwners`,
  `TakingFromAnotherPlayer_IsRefused`.
- `AFixture_CantBeStashedOrDeposited_EvenByAnOwner`, `AFixture_CanStillBeCarriedInTheHand`,
  `TheFixtureList_MatchesByPrefabOrAuthoredKey`.
- `AnEssential_CantBeDeposited_EvenByItsOwner`, `AnEssential_IsStashedAsUsual`.

Scenarios (`DevTools/DvmpScenarios/Scenarios/`):

- `fixture-no-stash`: a fixture's stash is refused and the client doesn't keep it; an ordinary item
  still stashes.
- `deposit-refused` (`OWNER=absent`, `OWNER=host`, `OWNER=essential`): someone else's mug is refused
  `deposit-item-not-owned`, an essential `essential-not-in-containers`; the client's own mug deposits.
- `ledger-rows`: the host's recall of the client's essential (`requester-not-persistent-owner`,
  OI-ACC-05), its retrieval of the client's mug (`not-your-item`, OI-LIST-20) and its take of the
  client's lantern from the client's inventory (`take-from-player`, OI-ACC-07) are refused; a
  player host only.
- `recall TAKEN=1`: the host's player takes the client's dropped essential from the world, allowed.
