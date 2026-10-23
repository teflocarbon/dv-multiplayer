> Legacy: describes the item system before the refactor (names as on `beta` 04bfe277). Read it only as evidence of how the code behaves today, not as design. Replaced by the "Open" section of `items/ledger.md` (R3), each question asked again. The item docs start at [items/README.md](../../design/items/README.md).

# Item ownership: open questions

> Status: the game-design questions about item ownership that are still open (2026-10-23). What is
> decided and built is in [`owned-items.md`](owned-items.md), with each decision's date; this doc
> keeps only what isn't decided.

## When should ownership transfer?

Ownership never changes today, except when an unowned personal item gets its first owner
([owned-items.md](owned-items.md#what-an-owned-item-is)). Pickup alone is ambiguous: it could mean
theft, borrowing, cooperation, cleanup or a gift. Options:

- never automatically; an explicit gift action (from the owned-items list, or handing over with a
  confirm);
- when the owner deliberately puts an item into another player's inventory;
- after the recipient has held it for a set time;
- when an abandoned item is claimed.

The ledger supports a transfer: an owner change with a reason. Any transfer should be a named,
logged transition with its own events (`item.ownership-transfer-requested`, `-accepted`,
`-rejected`) and a cause that tells a gift from a purchase, an admin reassignment or a save restore.

If gifts change the owner, a gift also changes the item's Lost and Found destination at once.

## Items made by machines or printers

Outside gadgets. Recommended: owned by the player who operated or paid, born `Made`. Decide when one
is synced.

## Old installed gadgets with no owner

A save from before gadget PR 1 has installed gadgets with no `dvmpPersistentOwner`. Recommended:
unowned (they stay as they are, and anyone may remove them), rather than giving them to the career
owner.

## Permissions

Friends who may take your tools, or a company's items any member may retrieve, go through
`ItemAccess.Decide` ([owned-items.md](owned-items.md#permissions)). The permissions system, and what
it allows by default, is still to be designed.

## What players see

Built: the owned-items list, and red for another player's recallable essential. Decided but not
built: red for every item another player owns. Still open:

- an owner label on an item that someone owns but you can't be made to give back;
- a borrowed indicator, when the holder isn't the owner;
- showing an item's Lost and Found destination outside the debug UI.

## Companies

The outline is decided (2026-10-20, [owned-items.md](owned-items.md#companies)). The details (owner
kinds, the company Lost and Found, what leaving a company does to its items in your hands) wait for
the company career's design, still to be written.
