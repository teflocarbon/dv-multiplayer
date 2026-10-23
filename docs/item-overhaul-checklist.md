# Item overhaul checklist

Every requirement of the item overhaul (owned items and the item lifecycle), one row each, so each
can be ticked off as it's built. Started 2026-10-23, checked against the code on beta 1a0b732f.

The owned-items plan was built PR by PR, and each PR was called done when its own, often narrowed,
scenarios passed. What a PR cut or deferred was written only inside its own section of the design,
so nothing carried it forward. This list is where that goes now.

## How to use this

- **Every item PR lists the IDs it closes** in its description, and ticks them here in the same PR.
- **A box is ticked only with merged code plus a proving scenario on both host kinds** (a player
  host and a dedicated host), or a Core unit test for a rule that lives only in `Multiplayer.Core`.
  During the refactor a PR marks its rows `[~] #n` with the scenario that proves them, and the
  full runs tick them (OI-REF-02 after R4, OI-REF-01 at the end; see [The refactor](#the-refactor)).
  A ticked row names the PR, the code and the scenario or test. A doc saying it's built isn't proof.
- **Scope a PR cuts or defers becomes a new unticked row in that PR**, with the next free ID in its
  area. Never "left for later" only in prose.
- **"Done" for a stack means its rows are ticked**, not that its PRs merged.
- **A `needs decision` row waits for the user.** Don't build it, or change its rule, until they
  decide; then the row records the decision and its date and becomes `[ ]`.

Statuses:

| Status | Meaning |
| --- | --- |
| `[x]` | Done: merged, with the code and a passing scenario (both host kinds) or Core test named |
| `[~] #n` | In an open PR (or on a named branch, before its PR) |
| `[~] built, unproved` | The code is on beta, but nothing proves it. It needs a scenario or a test |
| `[ ]` | Not started |
| `needs decision` | A product decision for the user, never built |
| `paused: <workstream>` | Kept and unticked, but outside the item refactor; it waits for that workstream |
| `not needed: …` | The user decided it isn't needed; kept so the record stays complete |

Sources, as cited in the rows:

| Cite | Where |
| --- | --- |
| OI §… | The original design, `git show e3f25e66:docs/design/owned-items.md` (before the as-built rewrite) |
| LC §… | `git show e3f25e66:docs/design/item-lifecycle.md` |
| OQ §… | `git show e3f25e66:docs/design/item-ownership-open-questions.md` |
| #n | That PR's description (scope it cut or deferred) |
| Not built | Today's [owned-items.md](legacy/items/owned-items.md#not-built-yet), or the named doc's "Not built" / "Known issues" |
| F*n* | Finding *n* of the code-vs-doc mismatches listed in #322's description |
| BR *id* | [research/item-bug-review.md](research/item-bug-review.md) |
| Map | `items-ledger-bug-map.md` in the coordination notes (bugs whose home was "none") |
| SH, GD | [design/shops.md](design/shops.md), [design/gadgets.md](design/gadgets.md) |

Host kinds in proofs: **p** a player host, **d** a dedicated host. "p only" marks a step that needs a
second player, so it can run only on a player host. A pass "(p+d)" is the in-game batch recorded in
the named PR; scenario line numbers are at beta 1a0b732f.

## Where it stands

On 2026-10-23, at beta 1a0b732f:

| Area | Done | Built, unproved | In a PR or branch | Not started | Needs decision | Paused | Not needed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| The refactor's full runs | 0 | 0 | 0 | 2 | 0 | 0 | 0 |
| Identity and what is owned | 9 | 3 | 0 | 3 | 0 | 4 | 0 |
| The ledger as the source of truth | 6 | 3 | 0 | 6 | 0 | 0 | 1 |
| Joining, rejoining and leaving | 4 | 2 | 0 | 5 | 0 | 0 | 0 |
| Starting items | 4 | 3 | 1 | 2 | 0 | 0 | 0 |
| Essentials and claims | 5 | 0 | 1 | 9 | 0 | 0 | 1 |
| Inventory slots | 0 | 0 | 0 | 3 | 0 | 0 | 0 |
| Cold and live homes | 0 | 0 | 0 | 6 | 0 | 0 | 0 |
| Lost and Found | 10 | 4 | 0 | 8 | 0 | 0 | 0 |
| The list and retrieval | 7 | 7 | 0 | 2 | 0 | 6 | 0 |
| Lineage | 2 | 2 | 0 | 0 | 0 | 3 | 0 |
| Fixtures and `ItemAccess` | 5 | 0 | 0 | 3 | 0 | 7 | 0 |
| Containers | 2 | 1 | 0 | 11 | 0 | 1 | 0 |
| Persistence | 6 | 1 | 0 | 15 | 0 | 0 | 1 |
| Projection and world sync | 1 | 2 | 0 | 13 | 0 | 4 | 0 |
| Physics and train wake | 0 | 1 | 0 | 11 | 0 | 3 | 0 |
| Vanilla paths | 0 | 0 | 0 | 6 | 0 | 0 | 0 |
| Layout and names | 0 | 0 | 0 | 1 | 0 | 0 | 0 |
| Shops seam | 1 | 0 | 0 | 0 | 0 | 9 | 0 |
| Gadgets seam | 1 | 1 | 0 | 3 | 0 | 6 | 0 |
| Mod items | 0 | 0 | 0 | 0 | 0 | 5 | 0 |
| Migration | 0 | 0 | 0 | 0 | 0 | 0 | 7 |
| **All 262** | **63** | **30** | **2** | **109** | **0** | **48** | **10** |

- **The item refactor** (user, 2026-10-23; the plan is the item-refactor-plan session's
  `docs/design/item-refactor.md`) builds the rows that serve storing items, giving them back and
  never losing them.
  - **Paused:** rows outside it are `paused: shops`, `gadgets`, `companies`, `physics and wake
    fidelity`, `list wording`, `not about loss` (the plan's D4) or `mod items` (D11). They stay rows, unticked, for
    that workstream. OI-PHY-05 and OI-PHY-12 are in the refactor.
  - **Not needed:** old saves start every player's items fresh (D1), so the migration rows aren't
    needed.
- **Nothing in the refactor waits for the user.** OI-COLD-06 was decided on 2026-10-24 (data only on
  the server, D12). Before it, the last four (job papers, OI-ID-12 and OI-LF-17; a leaver's hand
  item, OI-JOIN-06; red, OI-ESS-12) were decided on 2026-10-23 (the plan's D3 and D5) and are in the
  refactor. So are the one-home check (OI-LED-15, D7) and recreating an unloadable item
  (OI-LED-16, D9). Paused rows that still need a decision when their workstream resumes: OI-ID-16,
  OI-ID-17, OI-FIX-03, OI-FIX-04, OI-SYNC-12 and OI-GAD-08.
- **Not needed** also covers OI-PER-02 and OI-ESS-16 (rebuilds and repairs of old saves, D1).
- **Built, unproved** rows each need a scenario (or a Core test) before they can be ticked.
- **The 49 findings** each carry the item-review session's triage (2026-10-23, from the code): 15
  real bugs, 16 decided but not built, 18 doc-only. A doc-only row is one where the code does
  something reasonable that the old design described differently. The user decided them on
  2026-10-23:
  - seven are real gaps and keep the design's rule: OI-CON-08, OI-SYNC-09, OI-SYNC-10, OI-PHY-05,
    OI-PHY-11, OI-PHY-12 and OI-GAD-06;
  - the other 11 are rewritten to the code's rule. Each stays unticked until the new doc and a
    scenario or test prove it.
- Every one of the 49 findings and every id in the bug review has a row.

## The refactor

The plan is [design/item-refactor.md](design/item-refactor.md) (2026-10-23). Its PRs land on
`items/refactor` and each ticks the rows it closes here. Every row not paused or not needed is in
exactly one PR below; "re-proves" lists done rows whose code a PR rewrites, which the full runs
prove again without unticking them (OI-REF-02 and OI-REF-01, below).

| PR | Closes | Re-proves |
| --- | --- | --- |
| R0 Rename | none (OI-LAY-01 starts here, ticks in R8) | the item regression set and the known bugs, same results before and after |
| R1 Identity only | OI-LED-06, OI-LED-07, OI-ESS-05, OI-ACC-08, OI-LF-06, OI-JOIN-09, OI-ID-08, OI-CON-08 | OI-JOIN-03, OI-LF-05 |
| R2 The wire | OI-ID-07, OI-ESS-10, OI-ESS-12, OI-CON-07, OI-SYNC-19, OI-SYNC-20, OI-LIST-17, OI-LIST-22 | OI-ESS-06 |
| R3 The ledger decides | OI-LED-04, OI-COLD-01, OI-COLD-02, OI-COLD-04, OI-COLD-05, OI-COLD-06, OI-LED-05, OI-LED-08, OI-LED-09, OI-LED-10, OI-LED-13, OI-LED-15, OI-GAD-10, OI-ID-05, OI-ID-06, OI-ID-13, OI-ID-18, OI-ESS-01, OI-ESS-02, OI-ESS-03, OI-ESS-04, OI-ESS-08, OI-ESS-09, OI-START-04, OI-START-06, OI-START-08, OI-START-09, OI-SLOT-01, OI-SLOT-03, OI-ACC-05, OI-ACC-07, OI-LF-08, OI-LF-20, OI-GAD-04, OI-LIST-12, OI-LIST-20, OI-LIST-21, OI-LIN-07, OI-ID-19 | OI-ID-01, OI-ID-09, OI-ID-10, OI-ID-11, OI-LED-01, OI-LED-03, OI-LED-12, OI-ESS-14, OI-ESS-15, OI-START-02, OI-START-03, OI-START-05, OI-LIST-01, OI-LIST-02, OI-LIST-03, OI-LIST-08, OI-LIST-11, OI-LIST-19, OI-LIN-01, OI-LIN-06, OI-ACC-01, OI-ACC-02, OI-ACC-03, OI-FIX-01 |
| R4 Saving and loading | OI-PER-01, OI-COLD-03, OI-PER-03, OI-PER-04, OI-PER-06, OI-PER-07, OI-PER-09, OI-PER-10, OI-PER-12, OI-PER-13, OI-PER-14, OI-PER-15, OI-PER-16, OI-PER-17, OI-PER-18, OI-PER-19, OI-PER-20, OI-PER-21, OI-PER-22, OI-PER-23, OI-SLOT-02, OI-JOIN-01, OI-JOIN-02, OI-JOIN-05, OI-JOIN-07, OI-JOIN-08, OI-LED-16, OI-LF-16, OI-LF-21, OI-LF-22, OI-CON-04, OI-CON-05, OI-START-01, OI-GAD-01, OI-GAD-02, OI-ID-02, OI-ID-03, OI-ID-04 | OI-LF-15; OI-LED-02, OI-PER-05 and OI-PER-08 are rewritten to the new format (no stores to reconcile, no `Migrated`, no `Missing`) |
| R5 Containers on the ledger | OI-CON-02, OI-CON-03, OI-CON-06, OI-CON-09, OI-CON-11, OI-CON-12, OI-CON-13, OI-CON-15, OI-ACC-04, OI-LIST-18 | OI-CON-01, OI-LIST-10, OI-LF-07, OI-ESS-07 |
| R6 Every vanilla path | OI-LF-02, OI-LF-04, OI-LF-10, OI-LF-11, OI-LF-17, OI-ID-12, OI-JOIN-06, OI-JOIN-10, OI-JOIN-11, OI-START-07, OI-START-10, OI-ESS-13, OI-LIST-06, OI-LIST-14, OI-LIN-05, OI-VAN-01, OI-VAN-02, OI-VAN-03, OI-VAN-04, OI-VAN-05, OI-VAN-06 | OI-LF-01, OI-LF-03, OI-LF-09, OI-LF-12, OI-LF-13, OI-LF-14, OI-JOIN-04 |
| R7 Leases, physics and projection | OI-ESS-11, OI-PHY-01, OI-PHY-02, OI-PHY-03, OI-PHY-04, OI-PHY-05, OI-PHY-06, OI-PHY-07, OI-PHY-08, OI-PHY-11, OI-PHY-12, OI-PER-11, OI-SYNC-04, OI-SYNC-07, OI-SYNC-08, OI-SYNC-09, OI-SYNC-10, OI-SYNC-11, OI-SYNC-13, OI-SYNC-17, OI-SYNC-18, OI-LIST-09, OI-GAD-06 | OI-SYNC-01 |
| R8 Docs | OI-LED-14, OI-LF-18, OI-LF-19, OI-CON-10, OI-SYNC-02, OI-SYNC-03, OI-SYNC-05, OI-SYNC-06, OI-PHY-09, OI-PHY-10, OI-LAY-01 | |

**Testing during the refactor** (user, 2026-10-24: hybrid). R1 ran the full item regression set on
a player host and ticked its rows "(p; d: end pass)". From R2 on, each PR runs only its own new or
changed scenarios, once, on a player host, and marks the rows it closes `[~] #n`, naming the
scenario and its result. A row stays `[~] #n` after its PR merges into `items/refactor`. The full
runs tick them: a checkpoint after R4 on a player host (OI-REF-02), and the end pass on both host
kinds before the beta merge (OI-REF-01). A row whose proof fails there is fixed or unticked, and
every other difference becomes a row.

| ID | Requirement | Source | Status | Evidence |
| --- | --- | --- | --- | --- |
| OI-REF-02 | The checkpoint after R4: the item regression set and the known bugs on a player host over `items/refactor`. Every `[~] #n` row of R2, R3 and R4 is proved and ticked "(p; d: end pass)" or fixed first; a new failure is traced to the PR that caused it | User 2026-10-24 | `[ ]` | R3 and R4 are the big rewrites, so a failure is found while it's two PRs to search |
| OI-REF-01 | The end pass: the item regression set and the known bugs on both host kinds over `items/refactor`, before the beta merge. Every `[~] #n` row is proved on a player host and ticked; every row ticked "(p; d: end pass)" is proved on the dedicated host or unticked; every difference between the host kinds is fixed or becomes a row | User 2026-10-24 | `[ ]` | Replaces each PR's full runs |

OI-SHOP-01 stays done; its workstream is paused.

The item design docs the rows cite (`owned-items.md`, `item-lifecycle.md` and the rest) are legacy
(the user, 2026-10-23): evidence of the code before the refactor, not design. They are in
`docs/legacy/items/` (moved 2026-10-24), each replaced under `docs/design/items/` by its PR.

## Identity and what is owned

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-ID-01 | Every owned item has one random GUID for its whole life, minted once when the server registers it | OI §Decisions, §One id for life | `[x]` | #282 · `LiveItems.Ensure` (`:96`), `ItemIdentity.Ensure` · `item-id-roundtrip` (p+d) |
| OI-ID-02 | A Lost and Found record's id is the item's own id | OI §One id for life; #282 | `[x]` | #282 · `LostAndFoundServer` · `item-id-roundtrip` :65, :76 (p+d) |
| OI-ID-03 | A cold-container record's id is the item's own id, and a withdrawn item keeps it, so it is saved again (the cold-withdrawal bug) | OI §Known bugs; #282 | `[x]` | #282 · `LiveItems.RegisterMaterializedColdItem` (`:754`) · `item-id-roundtrip` :44, :52-56 (p+d) |
| OI-ID-04 | An item without `ItemSaveData` (a mug) in vanilla's Lost and Found takes its id back from its entry at load | OI §Where the id travels; #282 | `[x]` | #282 · `LostAndFoundServer.cs:581` · `item-id-roundtrip` :73-76, a mug across the second restart (p+d) |
| OI-ID-05 | A second item registering with an id already in use: the first keeps it and its place, and the copy is re-minted and logged (`item.duplicate-persistent-id`) | OI §Duplicates; #282 | `[x]` | #282 · `ServerLedger.ClaimPersistentId` (`:59-79`) · `ledger-save` :116-119 (p+d): the clone gets an id of its own, the original keeps its id and place. The log line (`:69`) isn't checked. The copy's entry is OI-ID-18 |
| OI-ID-06 | Lost and Found's `DuplicatePersistentItemId` result is the same check, and `Restore`'s callers log it | OI §Duplicates, §1b | `[~] built, unproved` | `Restore`'s results are logged (`LostAndFoundServer.cs:709-720`); `Add`'s refusal at `:354` is rolled back but not logged. No scenario makes a duplicate entry |
| OI-ID-07 | The wire keeps the per-session NetId; clients never learn an item's GUID (the owned-items list uses a per-session handle) | OI §Decisions, §The owned-items list | `[~] built, unproved` | Item packets carry the NetId; the list uses a handle (#308). No test asserts that no GUID reaches a client |
| OI-ID-08 | A re-keyed identity (`InstallGuidRekey`) re-keys the ledger's owners and holders and `OwnedItemsV1`, so the player keeps their list, retrieval and essentials | F3 | `[x]` | #331 · `ItemLedger.Rekey`, `InstallGuidRekey.Rekey`/`RekeySave` (`OwnedItemsV1` owners, holders, `born.by`, players) · `rekey` scenario (p; d: end pass); Core `ItemLedgerTests.Rekey_*` |
| OI-ID-09 | An unowned personal (`BelongsToPlayer`) item becomes owned by the first player to put it in a hand or inventory, born `Claimed`; pickup never transfers an existing owner | LC §Ownership establishment; OI §What an owned item is | `[x]` | Pre-overhaul (9e641a2), kept by #304 · `ItemStateMachine.cs:175` (`MayEstablishPersistentOwner`) · `lost-and-found` :46, `absent-owner` :50-53 (p+d); Core `ItemStateMachineTests`, `AbsentOwnerTests`. Born `Claimed` isn't checked (OI-LIN-07) |
| OI-ID-10 | A non-personal item stays unowned even when carried, so it is never listed, retrievable or sent to Lost and Found | OI §Decisions ("Found items aren't yours"); LC §Forbidden | `[x]` | `ItemStateMachine.cs:175` · Core `ItemStateMachineTests.NonPersonalItemClaim_DoesNotEstablishPersistentOwnership` (a Core rule). The one scenario that checks it in game is a KnownBug (`item-bug-car-delete-unowned` :35). Saving a carried one is OI-PER-19 |
| OI-ID-11 | Ownership changes only through a named, accepted server transition; every accepted transition advances the revision, and a refused one changes nothing | LC §Canonical lifecycle; OQ §Invariants | `[x]` | `ItemStateMachine` · Core `ItemStateMachineTests.AcceptedTransition_HasOnePlacementAndIncreasesRevisionExactlyOnce`, `StaleRevision_IsRejectedWithoutChangingCanonicalState`, `RecallByNonOwner_IsRejectedWithoutMutation`, the transition fuzz test |
| OI-ID-12 | Job papers (booklets, overviews, reports) are the job's, never owned items or listed | OI §Decisions; F7; refactor D5 | `[ ]` | **Decided, in the refactor (user 2026-10-23, D5):** job papers are never owned or in the ledger. Nothing excludes them today from the first-taker rule (`ItemStateMachine.cs:174-177`) or the ledger. Item-review triage: decided, not built. An owned booklet also becomes Missing at each load |
| OI-ID-13 | An admin `/give` is owned by its target, born `Granted` | OI §What an owned item is | `[~] built, unproved` | `AdminItemGrants.cs:117-148` sets the owner and moves it `Granted`, which the ledger takes as its birth (`ServerLedger.cs:411`). `owned-items` :53 checks only that the item arrives |
| OI-ID-14 | An owner can be a company (an owner kind beside the GUID); gadgets, garage keys, loco paint, the MU controller and the loco remote are company-owned | OI §Companies | `paused: companies` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Decided in outline 2026-10-21; details are company-career.md's |
| OI-ID-15 | A company Lost and Found, and retrieval and red by membership | OI §Companies | `paused: companies` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. As OI-ID-14 |
| OI-ID-16 | Giving an item to another player (an explicit gift that changes the owner) | OI §Open decisions; OQ §When should ownership transfer | `paused: not about loss` | Paused for the item refactor (D4, user 2026-10-23); was needs decision. Open since 2026-10-21. The ledger supports an owner change with a reason |
| OI-ID-17 | Items made by machines or printers (outside gadgets): owned by whoever operated or paid, born `Made` | OI §Open decisions; OQ §Which creation paths | `paused: not about loss` | Paused for the item refactor (D4, user 2026-10-23); was needs decision. Recommended, not decided. Decide when one is synced |
| OI-ID-18 | A re-minted copy's ledger entry is born `Duplicate`, with the original's id as `from` | OI §Duplicates, §Lineage | `[ ]` | `ClaimPersistentId` re-mints before the ledger sees the copy, so a live copy is born `Claimed` (or not at all, if unowned) with no `from`. Only an id the ledger already holds reaches `Duplicate` (`ItemLedger.cs:137-145`). Found by this checklist |
| OI-ID-19 | A re-key onto a Steam id that already has data merges the install GUID's items into it (ledger entries, inventory slots, Lost and Found, containers, claims), never skipping it: an item whose slot is taken goes to that player's Lost and Found, so nothing is stranded under the old GUID | F3; #331 review | `[ ]` | `InstallGuidRekey.RekeyIfUnclaimed` (`:45`) returns without re-keying when the Steam id `Owns` anything, so a player who played under the install GUID and also under Steam keeps the install GUID's items under an identity no one logs in as. Predates R1; found reviewing #331, user 2026-10-24: fix in R3 |

## The ledger as the source of truth

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-LED-01 | The ledger in Core: one entry per owned item, live or ended, with owner, prefab, claim slot, `where`, `shopStocked`, born and ended | OI §The ledger, §1a | `[x]` | #278 · `Multiplayer.Core/Items/Ledger/ItemLedger.cs`, `LedgerEntry.cs` · Core `ItemLedgerTests` |
| OI-LED-02 | The load table as a plan of steps (Keep, Correct, Revive, DropCopy, MarkMissing, Register, RecoverToLostAndFound) | OI §Loading, §1a | `[x]` | #278 · `Multiplayer.Core/Items/SaveLoad/LoadReconciler.cs` · Core `LoadReconcilerTests` (the plan only; carrying it out is OI-PER-05 to -08) |
| OI-LED-03 | The server's ledger follows every committed move and removal of an owned item, and cold deposits | OI §The ledger, §1b | `[x]` | #282 · `ServerLedger.OnMove` / `OnRemoved` (`:82`, `:103`), called from `ItemChangeLog` · `ledger-save` `CheckAll` and audit, `item-id-roundtrip` (p+d) |
| OI-LED-04 | Only an accepted transition moves the ledger: a refused one (`stale-authority-revision`, the `regrab-late` race) leaves it as it was | OI §Making vanilla agree | `[~] built, unproved` | The ledger follows `ItemChangeLog`, which logs accepted transitions only. No scenario checks the ledger after a refused transition |
| OI-LED-05 | **The ledger is the source of owners:** the server's record reads its owner from the ledger, and `PersistentOwnerPlayerId` is no longer stored (worked out from the identity's connected session, or none) | OI §Keyed by identity everywhere; Not built; F2 | `[ ]` | 2a narrowed this to the absent number (#304). The records still own ownership and the ledger follows them (`ServerLedger.cs:82-100`). Item-review triage: decided, not built. |
| OI-LED-06 | **Every server check by identity:** Lost and Found, recall, claim slots, containers, the disconnect rules, the first-taker rule and `ItemAccess`. Player numbers only on the wire | OI §Keyed by identity everywhere; F2 | `[x]` | #331 · `LiveItem.Owner`/`Holder` (Guid), `PlayerNumbers` (the wire edge), `ItemStateMachine` by `Actor`/`Requester`, `ItemAccessServer.Allows`, `LostItemCollectionPolicy.Owner`, `ContainerServer`, `ServerItemManager.DropHeldItemsOfDepartingPlayer` · regression set (p; d: end pass); Core `ItemStateMachineTests`, `LostAndFoundTests` |
| OI-LED-07 | `UnbindPersistentOwner`/`RebindPersistentOwner`, Lost and Found's `UnbindOwner`/`RebindOwner` and its per-entry `OwnerPlayerId` are gone: nothing is bound to a number, so nothing re-binds at login | OI §Keyed by identity everywhere, §What goes away; F2 | `[x]` | #331 · removed: `LiveItems.UnbindPersistentOwner`/`RebindPersistentOwner`, `LostItemRegistry.UnbindOwner`/`RebindOwner`, `LostItemRecord.OwnerPlayerId`, `ServerPlayer.OwnedItems` · `owner-offline`, `absent-owner` (p; d: end pass); Core `LostAndFoundTests.AnOwnersEntries_AreTheirsWhetherOrNotTheyAreConnected` |
| OI-LED-08 | Vanilla never moves an owned item; a check reports an owned item in vanilla's Lost and Found list whose `where` isn't `LostAndFound`, and any item in a dedicated host's own vanilla inventory | OI §Making vanilla agree | `[~] built, unproved` | `ServerLedger.Audit` (`:232-266`) reports an owned item in vanilla's Lost and Found whose *record* (not ledger entry) isn't there, and anything in a dedicated host's vanilla inventory; but that line carries no id, so the scenarios' `AuditFor` filters it out and it never fails a run. It skips `Container` records, live entries with no record, and items in several vanilla storages |
| OI-LED-09 | The check runs in debug builds as well as from the `ledger-*` scenarios | OI §Making vanilla agree, §1b | `[ ]` | Only scenarios call `Audit` (`DevTools/DvmpScenarios/Library/OwnedItems.cs:78, 88`) |
| OI-LED-10 | Inactive is not gone: the ledger and the load check find items by id among inactive objects too, never by `FindObjectsOfType` | OI §Making vanilla agree | `[~] built, unproved` | Lookups go through the registry and `NetworkedItem`'s id maps, never `FindObjectsOfType`. No scenario checks an inactive (LOD'd) item |
| OI-LED-11 | `Missing`: an entry whose store didn't hold it at load is kept, shown in the list as lost track of, and checked again at each load | OI §Loading, §The owned-items list | `not needed: replaced by OI-LED-16 (user 2026-10-23, D9)` | Was built, unproved: `MarkMissing` keeps the entry live and later loads re-check it (`LoadReconciler.cs:134-139, 182-185`); the list shows it as "missing". No scenario makes an item go missing. D9 (user 2026-10-23) replaces `Missing` with OI-LED-16 |
| OI-LED-12 | Ended entries are kept for lineage, the newest 100 per owner, and pruned beyond that | OI §In the vanilla save | `[x]` | #278 · `ItemLedger.cs` · Core `ItemLedgerTests` |
| OI-LED-13 | A dvmp eval helper dumps the ledger (`Ledger.Dump()`) | OI §1b | `[ ]` | None. `ServerLedger.Describe` (`:269`) has no callers; scenarios read entries through `Library/OwnedItems.cs` |
| OI-LED-14 | Code comments cite item docs that exist: none points at a removed section or describes the shadow ledger | F14; F2 | `[ ]` | `ServerLedger.cs:19-24, 354-356`; `LiveItems.cs:764`; `LostAndFoundServer.cs:488-489`. Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-LED-15 | Every owned item has exactly one home, checked at load, at every save and (in Debug) every few seconds, and asserted by scenarios after every run. In Debug a breach fails loudly; in Release it is logged and repaired: the item goes to the ledger's home, else to its owner's Lost and Found (`SaveRecovery`) | Refactor D7 (user 2026-10-23) | `[ ]` | Decided, in the refactor. Today's `Audit` is the nearest (OI-LED-08, -09) |
| OI-LED-16 | An entry whose saved state is missing or won't load is recreated fresh from its prefab, with the same id, in its owner's Lost and Found (`SaveRecovery`), with an error logged. If the prefab itself is gone (an item mod removed), the entry stays `Unloadable` with its saved state kept verbatim and is retried at each load | Refactor D9 (user 2026-10-23) | `[ ]` | Decided, in the refactor. It replaces `Missing` (OI-LED-11) |

## Joining, rejoining and leaving

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-JOIN-01 | A returning player gets their possessions back in their inventory at the saved slot (a hand item in the inventory), for a client and for the host's own player | OI §Joining and rejoining | `[x]` | Pre-overhaul (325c2fb) · `ItemSaveLoad.OnOwnerConnected` · `rejoin` :56, `inventory-persist` :44-45 (p+d); `host-player-persist` for the host's own player (p, by nature) |
| OI-JOIN-02 | Possessions at rejoin are read from the ledger (`With(identity)`), not from `WorldItemsV1` records held back at load | OI §Joining and rejoining; F5 | `[ ]` | `ItemSaveLoad.cs:67-86` reads the held-back records. Item-review triage: decided, not built. |
| OI-JOIN-03 | An offline owner keeps their items: an identity with no session, never nobody, so the first-taker rule, other players' recalls and vanilla's Lost and Found never take them | #304; OI §2a | `[x]` | #304; #331 · `LiveItem.Owner` is the identity, `PlayerNumbers.IsConnected`; on the wire until R2 `ItemUpdateData.OfflineOwnerNumber` · `absent-owner`, `owner-offline` check an identity with no session (p; d: end pass); Core `ItemStateMachineTests.TakingAnOfflineOwnersPersonalItem_*`, `NobodyElseRecallsAnOfflineOwnersEssential` |
| OI-JOIN-04 | A departing borrower's item (another player's, in hand, inventory or belt) goes to its owner's Lost and Found at once (`BorrowerLeft`), whether the owner is online or not | OI §Leaving; #305 | `[x]` | #305 · `ServerItemManager.PlayerItems.cs:133` (`BorrowerLeft`) · `borrowed-disconnect` :71-74 with `OWNER=absent` (+`HOLD=hand`, +`LEAVE=crash`) (p+d); `OWNER=host` (p only) |
| OI-JOIN-05 | A borrowed item is never saved under the borrower, and a leave never makes a second copy | OQ §Invariants; OI §Leaving | `[~] built, unproved` | `borrowed-disconnect` checks the owner's Lost and Found only; nothing checks the leaver's save or a second copy |
| OI-JOIN-06 | A leaving player's own or unowned held item goes into a free inventory slot; with none free, an owned one goes to their Lost and Found and an unowned one drops at their feet. Never left off its train, under a lease, or unsaved | OI §Leaving; #305 "left for later"; F9; BR SL2; refactor D5 | `[ ]` | **Decided, in the refactor (user 2026-10-23, D5).** Today it is dropped where they stand (`ServerItemManager.PlayerItems.cs:102-118`). Item-review triage: decided, not built. A found item in hand is lost |
| OI-JOIN-07 | Unowned items a leaver carries stay with them and come back at their next join | OI §Leaving | `[~] built, unproved` | No scenario. `rejoin` :49 checks that a held item goes to the world instead |
| OI-JOIN-08 | An offline player's saved possessions are only their own and unowned items (an item borrowed when the server restarts goes to its owner, BR SL1) | OI §Leaving, §Loading; BR SL1 | `[ ]` | SL1: saved under the borrower and released only to them (`WorldItemPersistenceSave.cs:64`, `WorldItemPersistenceRestore.cs:210-216`). Needs a restart without the client rejoining, which the pair harness can't do yet |
| OI-JOIN-09 | Login re-binds nothing, since nothing is keyed by number | OI §Joining and rejoining | `[x]` | #331 · `NetworkServer.AdmitPlayer` binds nothing (only `InstallGuidRekey.OnLogin`, `ItemSaveLoad.OnOwnerConnected`) · `owner-offline`, `rejoin`, `rekey` (p; d: end pass) |
| OI-JOIN-10 | A rejoined owner's item on the ground isn't collected just after a restart on a dedicated host (the owner's position isn't settled 10 s after loading) | BR D1; #282 | `[ ]` | `LostAndFoundServer.cs:727-730`; seen in `ledger-save` @dedicated |
| OI-JOIN-11 | A client standing in a loco's cab counts as near an item for Lost and Found's protection on a dedicated host | Map CAB-POS; OI §Found in testing | `[ ]` | Suspected `ServerPlayer.AbsoluteWorldPosition` while `CarId` is set. Needs a probe. Seen in `offline-lost-and-found` @dedicated |

## Starting items

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-START-01 | On a player's first join to the career the server makes the starter kit, owned by them, born `StarterKit`, into their inventory, and sets a saved marker (`players.<identity>.starterKit`) | OI §Joining and rejoining step 3, §4 | `[x]` | #306 · `StartingItemsIssuer` · `starting-items` :108-110 (p+d) |
| OI-START-02 | At every join the server makes each essential the player has no live item of, owned, born `Issued`, into a claim slot | OI §Joining and rejoining step 4, §4 | `[x]` | #306 · `StartingItemsIssuer.cs:83-92` · `starting-items` :57, :78-81 (p+d) |
| OI-START-03 | "Has" counts a live item anywhere (Lost and Found, another player, a container); an ended one doesn't. A destroyed essential is replaced only once its entry has ended | OI §Decisions, §4 | `[x]` | #306 · `StartingItemsIssuer.Has` (`:123`) · `starting-items` :83, :94 (p+d) |
| OI-START-04 | "Has" is read from the ledger alone, and `OwnedItemsSummary` is gone | OI §What goes away; F6 | `[ ]` | `StartingItemsIssuer.Has` falls back to it (`StartingItemsIssuer.cs:127`); its comment still describes removed join-packet fields (`OwnedItemsSummary.cs:9-19`). Item-review triage: decided, not built. |
| OI-START-05 | DV's `StartingItemsSafeguard` and `LicensesSafeguard` are off in every multiplayer game, and no game makes its own starting items: the join-packet fields and the rejoin swap are gone | OI §Joining and rejoining, §What goes away, §4 | `[x]` | #306 · `Shared/Career/StartingItemsControllerPatch.cs` · `starting-items` (each essential once, :127) and `rejoin` :56 (no unbound duplicates) (p+d). `starting-items` counts ledger entries, not the client's inventory |
| OI-START-06 | The host's own player gets its starting items from the server too | #306 | `[~] built, unproved` | The issuer runs from `CompleteLoading`, so the host's player too. `starting-items` covers the client only; `host-player-persist` checks for duplicates, not issuing |
| OI-START-07 | No licence papers are made in multiplayer; a licence is an entitlement only | OI §Joining and rejoining step 5, §4 | `[~] built, unproved` | `Shared/Career/PhysicalLicensePaperPatch.cs` returns null from the four `CreateLicense` overloads while a session runs. No scenario |
| OI-START-08 | If the ledger never loads, nothing is made, and the next join tries again | #306; OI §4 | `[~] built, unproved` | `StartingItemsIssuer` waits for the load check. No scenario makes the load fail |
| OI-START-09 | An issued (or restored) essential with no free slot goes to the owner's Lost and Found (`InventoryOverflow`), never to no slot at the world origin; the issuer sees the player's ordinary items when it picks a slot | BR E2a; OI §Lost and Found reasons | `[~]` branch `items/essential-overflow` (item-review, stacked on #324) | KnownBug `item-bug-full-inventory-essential`. The issuer route only; the other routes are OI-START-10 |
| OI-START-10 | An item put into a full inventory by any route never ends up in no slot: a Lost and Found retrieval, a container withdrawal (BR LF3) and a recall land in a slot or fall back | BR E2a, LF3; item-review's scope cut from OI-START-09; deferred by the user 2026-10-23 | `[ ]` | The client counts a refused Reserved claim as placed (`NetworkedItem.StateApplication.cs:673-679`). Deferred by the user, unticked |

## Essentials and claims

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-ESS-01 | Claim slots are assigned by the ledger, unique per owner by construction, when an essential is issued or first stashed, and the runtime repair (`EnsureDistinctClaimSlots`) goes | OI §Joining and rejoining ("Claim slots"), §What goes away; F4 | `[ ]` | The repair still runs after transitions, recalls, retrievals and at owner load (`LiveItems.cs:358,593,702`; `NetworkServer.cs:1202`). Item-review triage: decided, not built. The one-off repair for old saves is OI-ESS-16 |
| OI-ESS-02 | The issuer and the ledger's slot policy agree on which slot a new claim takes | F4; BR E4 | `[ ]` | The issuer picks the lowest free slot (`StartingItemsIssuer.cs:87`), the policy the highest. Item-review triage: decided, not built. |
| OI-ESS-03 | Claim repair and make-room don't aim at the VR belt (slots 33-35), and don't rank by a slot the server may have wrong | BR E4 | `[ ]` | `ClaimSlotPolicy.cs:82-86`, `InventoryIntegration.cs:209`. Code only |
| OI-ESS-04 | The host's own player's claim clashes are repaired too | BR E5 | `[ ]` | It skips `ReadyForItems` (`NetworkServer.cs:1062-1066`). Moot once OI-ESS-01 lands |
| OI-ESS-05 | Recall of an essential checks the owner's identity, not their number | OI §Retrieval ("The essential recall") | `[x]` | #331 · `ItemStateMachine.Recall` (`Owner == Requester`), `LiveItems.TryRecall` · `recall` (p; d: end pass); Core `ItemStateMachineTests.NobodyElseRecallsAnOfflineOwnersEssential` |
| OI-ESS-06 | Recall: a non-owner taking a claimed essential marks it `Stolen`, the owner sees the red silhouette and recalls it into its slot | LC §Retrieval claim slots | `[x]` | Pre-overhaul · `LiveItems` owner recall · `recall` :40-51 (p+d), `TAKEN=1` :53 (p only) |
| OI-ESS-07 | Essentials are refused from containers (`essential-not-in-containers`) and stay in the inventory | OI §Decisions (2026-10-22); #311; BR LF7 | `[x]` | #311 · `ItemAccess.cs` · `deposit-refused OWNER=essential` :39-41 (p+d); Core `ItemAccessTests.AnEssential_CantBeDeposited_EvenByItsOwner` |
| OI-ESS-08 | An essential in its owner's Lost and Found still has its silhouette after the owner rejoins | BR E10 | `[~] #324` | A rejoining owner got the Lost and Found tombstone without the item. KnownBug `item-bug-dropped-essential-rejoin`, a required pass once #324 lands |
| OI-ESS-09 | A silhouette that arrives after an ordinary item took its slot makes room or is retried, never refused for good | BR E1 | `[ ]` | `InventoryIntegration.cs:141-171`. Code only: it held in 6 rejoins (`item-bug-dropped-essential-rejoin LOST=0`) |
| OI-ESS-10 | A client never restores another owner's claim onto a slot that one of its own essentials claims (two radios on slot 0) | BR E2b | `[ ]` | `StateApplication.cs:814-821`, `InventoryIntegration.cs:203-206`. Code only |
| OI-ESS-11 | An essential recalled out of a moving cab ends its physics lease, so a save writes it in the inventory | BR SL4 | `[ ]` | KnownBug `item-bug-recall-lease`. The recall commits through `ApplyCore`, which skips the lease revoke (`LiveItems.cs:572`) |
| OI-ESS-12 | **Red for every item someone else owns**, online or not: item updates gain an "owned by someone not here" bit, and `InventoryPresentationPlanner` shows red for any owner but the local player (moves the protocol fingerprint) | OI §Decisions, §Retrieval ("Red"), §2b; F8; Not built; refactor D3 | `[ ]` | **Decided, in the refactor (user 2026-10-23, D3):** red for every item someone else owns. The refactor's wire tells a client each item's relation (mine, someone else's, nobody's) in place of the owner bit. The planned PR 2b was never written. Today only a recallable essential shows red (`InventoryPresentation.cs:68-70`). Item-review triage: decided, not built. |
| OI-ESS-13 | Vanilla's inventory hazards (H1-H8: a stale reserved map after a swap, load slot collisions, a refused storage move leaving an item in none, ...) can't hide or lose an item | Map ("V H1-H8"); BR §Vanilla facts | `[ ]` | No home. #306 avoids part of H2 |
| OI-ESS-14 | No game makes essentials or the kit, so DV's safeguard can't put a fresh essential on a dropped one's slot (E3) or count a borrowed one as its own (E9), no duplicate discard can destroy the wrong copy (E7), and no kit lives only on a client (E8) | BR E3, E7, E8, E9 | `[x]` | #306, #307 · the code paths are deleted (`StartingItemsControllerPatch` keeps the safeguards off) · `starting-items`, `rejoin` :56 (p+d) |
| OI-ESS-15 | An essential moved to another slot, with an ordinary item put in its old one, is back in its own slot after a rejoin or a restart, for a client and for the host's player | BR E6 | `[x]` | Claim slots (`SlotClaimPlanner`) · `item-bug-essential-slot-rejoin` (+`RESTART=1`; `WHO=host` p only), passing as a regression check (p+d) |
| OI-ESS-16 | Migration repairs an old save's claim-slot clashes once | OI §Joining and rejoining ("Claim slots"), §Migration; split from OI-ESS-01 | `not needed: fresh items (user 2026-10-23, D1)` | Old saves start every player's items fresh. Core's one-off repair is built (#278) but unused |

## Inventory slots

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-SLOT-01 | The server records every inventory item's exact slot, owned or not (an owned item's ledger home is `Slot(holder, slot)`); moving an item between slots is a request the server commits | [item-refactor.md](design/item-refactor.md) §What's wrong today; the user, 2026-10-23 (goal b) | `[ ]` | The record keeps a slot only for an essential's claim (`LiveItems.cs:261`), and a slot move is never sent. R2 (the request), R3 |
| OI-SLOT-02 | A save writes each inventory item's slot, and a reload or a rejoin puts it back in that slot, for a client and for the host's own player | [item-refactor.md](design/item-refactor.md); the user, 2026-10-23 (goal b) | `[ ]` | `WorldItemsV1` saves only `claimSlot`, and a restore fills from the front (`NetworkedItem.StateApplication.cs:677`). R4, scenario `inventory-slots RESTART=1` |
| OI-SLOT-03 | Two items never share one holder's slot on the server; a request into an occupied slot is refused | [item-refactor.md](design/item-refactor.md) §The ledger | `[ ]` | R3 |

## Cold and live homes

An item in a home nobody is using is data only, and every inventory is server data
([item-refactor.md](design/item-refactor.md) §The guarantee, D12; the user, 2026-10-24).

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-COLD-01 | An item whose home is cold (an offline player's inventory or claim slot, Lost and Found, a container) has no object on any game: only its saved state, and its ledger entry if owned | D12 | `[ ]` | R3. Today a leaver's inventory items stay live objects on the server (`ServerItemManager.PlayerItems`) and a reload makes them again |
| OI-COLD-02 | When a holder leaves, each item in their inventory goes cold in one ledger change: the server takes its state through `ItemSaveData` and destroys the object on every game; a hand item goes to a free slot first (D5) | D12 | `[ ]` | R3. Replaces "keep the originals" and the rejoin's restore of originals |
| OI-COLD-03 | When an owner joins, each cold item in their inventory is made from its state in its exact slot before their inventory is shown, for a client and for the host's own player | D12; OI-SLOT-02 | `[ ]` | R4 |
| OI-COLD-04 | No vanilla path, sweep or respawn can move or destroy a cold item, and the invariant check finds no object for an item in a cold home | D12 | `[ ]` | R3 |
| OI-COLD-05 | **Every inventory is server data, with optimistic clients:** a connected player's stash, slot move, hotbar switch, drop and pick-up show at once on their game (no wait) and go to the server as a request; the server commits each to the ledger and confirms, and only a refusal rolls the client back. The client never changes an inventory without telling the server | D12; OI-SLOT-01; R2 §The wire | `[ ]` | R3 (with R2's one request, one result) |
| OI-COLD-06 | **No duplicate on the server:** an item in a connected remote player's inventory is data only on the server, like a cold item; that player's game holds the only object | D12 (user, 2026-10-24: "keep data only") | `[ ]` | R3. First move what reads those objects on the server to the data: the job booklet tracked per player (`TryGetIssuedJobBooklet`), recall, and vanilla's inventory checks |

## Lost and Found

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-LF-01 | An online owner's items are collected from the world beyond 200 m, protected by another player within 100 m and a 10 s grace (unchanged) | OI §Lost and Found for every owned item | `[x]` | Pre-overhaul · `LostItemCollectionPolicy` (`Multiplayer.Core/Items/LostAndFound/LostItemCollectionPolicy.cs`) · `lost-and-found` :51 (p+d); Core `LostAndFoundTests` |
| OI-LF-02 | **Offline counts as far:** an offline owner's items are collected from the world, train interiors and shelves, under the same protection and grace | OI §Decisions, §Lost and Found for every owned item; #305 | `[~] built, unproved` | #305 · `LostAndFound.cs:74`. Proved on a player host (`offline-lost-and-found` :51-52; `owner-offline WHERE=cab` :84). On a dedicated host `offline-lost-and-found` fails (OI-JOIN-11), and no scenario puts an item on a shelf |
| OI-LF-03 | With nobody connected nothing is collected; the first player to join starts the sweep for items not near them | OI §Lost and Found for every owned item | `[x]` | #305 · `LostAndFoundServer` · `owner-offline` :78 (d; on a player host the host's player is always connected); Core `LostAndFoundTests.UnknownPositionsAndActiveContainment_FailSafe` |
| OI-LF-04 | An offline owner's attached and snapped items (an EOT lantern, a hanger) are collected too, detached first | OI §Lost and Found for every owned item; #305 "left for later"; Not built | `[ ]` | |
| OI-LF-05 | A Lost and Found record can be made for an offline owner (keyed by identity; no live owner number needed) | OI §Lost and Found for every owned item | `[x]` | #304; #331 · `LiveItems.RestoreLostRecord(item, Guid owner, ...)`, `LostAndFoundServer` · `car-delete-owner` (`OWNER=offline`, `absent`), `offline-lost-and-found` (p; d: end pass) |
| OI-LF-06 | `unowned-or-unbound` becomes `unowned`: an owner is never unbound | OI §Lost and Found for every owned item | `[x]` | #331 · `LostItemCollectionPolicy`: `unowned` (no owner identity), nothing unbinds (OI-LED-07) · Core `LostAndFoundAwayOwnerTests.AnUnownedItem_IsNeverCollected` |
| OI-LF-07 | A container goes to Lost and Found as one entry with its contents untouched, never one entry per item | OI §Lost and Found; LC §Forbidden | `[x]` | Pre-overhaul · `LostAndFoundServer.Collect` · `owned-retrieve` :66-70, the toolbox whole with the mug inside (p+d) |
| OI-LF-08 | One entry point by id, `SendToLostAndFound(id, reason, detail)`, for every system that sends an item home; it refuses an item that's with a player | OI §What other systems read; #305 | `[~] built, unproved` | #305 · `ServerLedger.SendToLostAndFound` (`:278`), used by retrieval (`Retrieval.cs:81`, so `owned-retrieve` exercises it). Its refusal of an item with a player isn't checked |
| OI-LF-09 | A deleted car sends its owned items to their owners' Lost and Found, an offline owner's too; it never destroys them | BR LF1; #304 | `[x]` | #304 · `LostAndFoundServer.CollectFromDeletedCar` · `car-delete-owner` :62-65 (p+d) |
| OI-LF-10 | An unowned item in a deleted car goes back into the world as in vanilla (a fixture back to its spot), never destroyed | BR LF2 | `[ ]` | KnownBug `item-bug-car-delete-unowned`. `CollectFromDeletedCar` destroys whatever `Collect` refuses (`LostAndFoundServer.cs:288-303`) |
| OI-LF-11 | `CarSpawner.DeleteTrainCarsFromTrack` sends a car's items to Lost and Found as other car deletes do | BR LF5 | `[ ]` | Unpatched (`CarDeleteLostAndFoundPatch.cs:15-29`). No vanilla caller found |
| OI-LF-12 | Vanilla's own Lost and Found path is blocked for an owned item, an offline owner's included | BR LF4; #304 | `[x]` | #304 · `StorageControllerPatch` · `owner-offline` :79 (d), :88 (p) |
| OI-LF-13 | A client forgets its inert copy when the server destroys an item that's in Lost and Found (LF-inert) | Map LF-inert; #308 | `[x]` | #308 · `NetworkedItemServer.OnDestroyed` · `car-delete-owner OWNER=online/absent` leak check (p+d) |
| OI-LF-14 | An item retrieved from Lost and Found is indexed for interest again, so a player who joins later (its owner rejoining) is sent it | #310 | `[x]` | #310 · `LostAndFoundServer.TryRetrieve` · `starting-items` (p+d; it failed on d before the fix) |
| OI-LF-15 | An entry lives as long as its item: the reconcile removes an entry whose item is gone or moved out, and a destroyed item isn't saved | LC §Lost and Found lifecycle | `[x]` | `LostItemEntryReconciler.Classify` (Core) · Core `LostAndFoundTests.Reconcile_*` |
| OI-LF-16 | Nothing expires: an absent player's items wait however long they're away | OI §Decisions | `[~] built, unproved` | No expiry exists in the code. Nothing proves an entry survives a long absence across restarts |
| OI-LF-17 | A lost job booklet goes to the Lost and Found of the player it was issued to, issued by identity, not number | OI §Lost and Found ("Job papers"); F7; refactor D5 | `[ ]` | **Decided, in the refactor (user 2026-10-23, D5),** with OI-ID-12. Issuance is by number today, used only for summoning (`NetworkedJob.cs:102-113`). Item-review triage: decided, not built. |
| OI-LF-18 | Lost and Found's distance checks measure from the item's live position on the server | LC §Placement and motion; F15 | `[ ]` | `BuildCandidate` uses `transform.position` (`LostAndFoundServer.cs:744-746`). Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-LF-19 | Retrieval revives the same object, which kept its NetId while it lay inert in vanilla's Lost and Found storage | LC §Lost and Found lifecycle; F16 | `[ ]` | The item keeps its NetId and object, inert, and is revived (`LostAndFoundServer.cs:155-210`). Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-LF-20 | Reasons on the wire, append only: `OwnerDistance` (offline owners too), `ContainerCleared`, `BorrowerLeft` and `RetrievedByOwner` in use | OI §Lost and Found ("Reasons") | `[~] built, unproved` | The paths that use them are proved (OI-LF-01, -02, -09, OI-JOIN-04, OI-LIST-08); no scenario checks the reason an entry records |
| OI-LF-21 | `SaveRecovery` is used by load reconciliation, by the one-home guarantee in a Release build, and for an unloadable item recreated fresh | OI §Lost and Found ("Reasons"); refactor D7, D9 | `[ ]` | Unused. See OI-PER-07, OI-PER-12, OI-LED-15, OI-LED-16 (OI-MIG-04's use isn't needed) |
| OI-LF-22 | `GadgetDestinationMissing` is used: an installed gadget whose car is gone at load goes to its owner's Lost and Found | OI §Lost and Found ("Reasons"); #305 "left for later"; GD §Persistence | `[ ]` | Not routed on beta. Gadget 3a (#314) uses this reason for a car deleted at runtime, not for a car missing at load |

## The list and retrieval

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-LIST-01 | An Owned Items page in the career manager lists the player's live items, built by the server from the ledger, each with a per-session handle and where it is | OI §The owned-items list; #308 | `[x]` | #308 · `Server/Items/OwnedList/OwnedItemRows.cs`, the client's `OwnedItemsList` · `owned-list` :72, :79-80 (p+d) |
| OI-LIST-02 | Ended items aren't listed: a used-up or destroyed item leaves the list | OI §Decisions ("The list shows only what still exists") | `[x]` | #308 · `OwnedItemRows` (live entries only) · `owned-list` :76, `item-lineage` :51-52 (p+d) |
| OI-LIST-03 | Where it is, worded on the server: with you; with another player; in a car's interior; on a shelf; in a container; in Lost and Found; installed on a car | OI §The owned-items list; #308 | `[x]` | #308 · `Multiplayer.Core/Items/OwnedList/PlaceText.cs` · Core `PlaceTextTests`. Which place the server picks is OI-LIST-21 |
| OI-LIST-04 | A lying item says where: "near Harbor (HB), 1.2 km", not "on the ground" | OI §The owned-items list | `paused: list wording` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. As built (#308) it says "on the ground" |
| OI-LIST-05 | A contained item names the nesting: "in your Toolbox, in your inventory" | OI §The owned-items list | `paused: list wording` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. As built (#308) it names the container only |
| OI-LIST-06 | An attached item says "on the end of car L-042"; a missing one says "lost track of it" | OI §The owned-items list | `[~] built, unproved` | Worded "attached to L-042" and "missing" (`PlaceText.cs:34-65`). No scenario lists either |
| OI-LIST-07 | The page's labels are localised (with the Lost Items page) | #308 "left for later" | `paused: list wording` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. English only |
| OI-LIST-08 | Retrieval sends the item to its owner's Lost and Found (`RetrievedByOwner`), with no nearby-player protection or grace; from the ground and a train interior | OI §Decisions, §Retrieval; #310 | `[x]` | #310 · `Server/Items/OwnedList/Retrieval.cs` · `owned-retrieve` :59-61, ground and cab (p+d) |
| OI-LIST-09 | Retrieval from a shelf (static parent) | OI §Retrieval | `[~] built, unproved` | `RetrievalRule` allows it. No scenario puts an item on a shelf |
| OI-LIST-10 | Retrieval from a container takes the outermost container that isn't with the owner, whole, as one entry | OI §Retrieval; #310 | `[x]` | #310 · `Retrieval` (walks up the cold graph) · `owned-retrieve` :66-70 (p+d) |
| OI-LIST-11 | Retrieval from another player's hand, even mid-use: it leaves their hand, they get a message, the change log names the owner | OI §Decisions, §Retrieval; #310 | `[x]` | #310 · `Retrieval` · `owned-retrieve` :84-88 (p only: needs the host's player). The holder's message and the change log aren't checked |
| OI-LIST-12 | Retrieval from another player's inventory or belt | OI §Retrieval; #310 | `[~] built, unproved` | Same path as the hand. No scenario retrieves from another player's inventory or belt |
| OI-LIST-13 | Retrieval from another player's hand stops a tool-use presence on the item | OI §Retrieval | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Tool-use presences are gadget PR 11 (#320, draft) |
| OI-LIST-14 | Attached and snapped items (an EOT lantern, a hanger) are detached and retrieved | OI §Retrieval; #310 "not in this PR"; Not built | `[ ]` | Refused as "not yet" |
| OI-LIST-15 | An installed gadget is retrieved from any car, any time: removed natively in every game with change reason `gadget-retrieved`, into Lost and Found | OI §Decisions, §Retrieval, §8 | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [~] #313. Draft, waits for gadget 3a (#314). #313 cut the `gadget-retrieved` reason (3a's change log records operations by player, not reason): a new row, OI-LIST-16 |
| OI-LIST-16 | The change log carries the `gadget-retrieved` reason for a retrieval | #313 "not done" | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Cut from #313 |
| OI-LIST-17 | Mid-transaction retrieval is refused, "busy, try again": mid-recall | OI §Retrieval; #310 | `[~] built, unproved` | `Retrieval.cs:70-74` (`busy-try-again` while a recall is pending). No scenario |
| OI-LIST-18 | Mid-transaction retrieval is refused for a container mid-withdrawal or mid-deposit | OI §Retrieval; #310 "not in this PR"; Not built | `[ ]` | |
| OI-LIST-19 | Nothing to do (with you, in Lost and Found, missing) is refused with its reason | OI §The owned-items list, §Retrieval; #310 | `[x]` | #310 · `RetrievalRule` (`PlaceText.cs:86-96`) · `owned-retrieve` :74-75 (`nothing-to-retrieve`, p+d); Core `RetrievalRuleTests` (Snapped and plain World left out) |
| OI-LIST-20 | Retrieval asks `ItemAccess` (Retrieve, owner only) | OI §Permissions; #310 | `[~] built, unproved` | `Retrieval.cs:75`. No scenario has someone else retrieve an item |
| OI-LIST-21 | The server picks the right place for an item in a hand, on a shelf, with another player, and installed | OI §The owned-items list; #308 | `[~] built, unproved` | `OwnedItemRows`, from the ledger and records. `owned-list` covers the inventory, ground, cab, Lost and Found and a toolbox only, and checks the toolbox row only for "in " |
| OI-LIST-22 | A refused retrieval is followed by a fresh list | OI §The owned-items list; #310 | `[~] built, unproved` | `NetworkServer.Items.cs:635-650` always sends the list after the result. No scenario checks it |

## Lineage

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-LIN-01 | `RecordReplacement`: a server-side swap makes the replacement the used item's owner's, born `Replaced` from it, and ends the used item with `replacedBy` | OI §Lineage, §7; #312 | `[x]` | #312 · `ServerLedger.RecordReplacement` (`:131`) · `item-lineage` :43-48 (p+d). The scenario calls it directly: no production code does yet (OI-LIN-02 to -04) |
| OI-LIN-02 | Duct tape's last use, through the gadget server, records its empty roll as the replacement | OI §Lineage, §7; #312 "left to gadget PRs" | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Gadget 4 (#315, draft) doesn't call `RecordReplacement` (checked on `origin/gadgets/mount-points`) |
| OI-LIN-03 | The solder spool's first use records its empty spool | OI §Lineage, §7; #312 | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Gadget 5 (#316, draft) doesn't call it |
| OI-LIN-04 | A label's `DestroyOnGadgetUnlink` records its replacement | OI §Lineage, §7; #312 | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Gadget 9 (#318, draft) runs the native swap server-side but doesn't call it |
| OI-LIN-05 | A money deposit ends its entry `MoneyDeposited`, with nothing replacing it | OI §Lineage, §7 | `[~] built, unproved` | `ServerLedger.OnRemoved` ends the entry with the removal cause. No scenario deposits money (`item-lineage` leaves it for the money work) |
| OI-LIN-06 | Any other destroy of an owned item ends its entry with its recorded removal cause (`DestroyedByGame` when unknown) | OI §Lineage; #312 | `[x]` | #282 · `ServerLedger.OnRemoved` (`:103`) · `item-lineage` :59-61, `starting-items` :83 (p+d) |
| OI-LIN-07 | Each way an item becomes owned records its birth reason, time and actor: `Purchased`, `StarterKit`, `Issued`, `Granted`, `Claimed`, `Replaced`, `Made`, `Migrated`, `Duplicate` | OI §What an owned item is, §Lineage | `[~] built, unproved` | Proved: `StarterKit`, `Issued` (`starting-items`), `Migrated` (`ledger-save`), `Replaced` (`item-lineage`, scenario-driven). Built, unproved: `Purchased` (`ShopDelivery.cs:44`), `Granted` (`AdminItemGrants.cs:125,148`), `Claimed` (the default). Not reachable: `Duplicate` (OI-ID-18). Never produced: `Made` (OI-ID-17) |

## Fixtures and `ItemAccess`

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-ACC-01 | One server-side decision point, `ItemAccess.Decide`, pure in Core, whose default policy is today's rules, for stash, belt, deposit, withdraw, take from the world, take from a player, remove a gadget, retrieve and recall | OI §Permissions, §9; #296 | `[x]` | #296 · `Multiplayer.Core/Items/Access/ItemAccess.cs` · Core `ItemAccessTests` |
| OI-ACC-02 | Wired: a stash or belt (any transition into a player's inventory) | #296 | `[x]` | #296 · `LiveItems.cs:279` · `fixture-no-stash` :29-37 (p+d). The belt is inventory slots; there is no separate Belt action |
| OI-ACC-03 | Wired: a cold-container deposit (owner only, own items only) | #296 | `[x]` | #296 · `ContainerServer.cs:158` · `deposit-refused` :52-63 (p+d) |
| OI-ACC-04 | Wired: withdraw (and the withdrawal uses the server's slot, not the client's: BR LF3) | #296 "not wired yet"; BR LF3 | `[ ]` | Not called. The withdrawal trusts the client's slot (`ContainerServer.cs:265, 382`) |
| OI-ACC-05 | Wired: recall | #296 "not wired yet" | `[ ]` | Not called |
| OI-ACC-06 | Wired: remove a gadget (the gadget server's request checks call it) | #296 "not wired yet"; OI §Permissions | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Not called on beta; gadget removal is gadget 3a (#314) |
| OI-ACC-07 | Wired: take from the world, take from a player | OI §9 | `[ ]` | Not called |
| OI-ACC-08 | `ItemAccessServer` decides "owns it" by identity | F2 | `[x]` | #331 · `ItemAccessServer.Allows` (`record.Owner == actor.Guid`) · `fixture-no-stash`, `deposit-refused` (p; d: end pass) |
| OI-ACC-09 | The permissions system (friends, company members) replaces the policy, not the call sites | OI §Permissions | `paused: shops (the permissions system)` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Later |
| OI-FIX-01 | A fixture can't go into an inventory, a belt or a container (`fixture-stays-in-its-place`) | OI §Fixtures; #296 | `[x]` | #296 · `ItemAccess.cs`, wired at `LiveItems.cs:279` and `ContainerServer.cs:158` · `fixture-no-stash` :29-30 (stash, p+d); Core `ItemAccessTests.AFixture_CantBeStashedOrDeposited_EvenByAnOwner`. No scenario deposits a fixture |
| OI-FIX-02 | The fixture list holds the shop scanner, one per shop (by authored key or prefab) | OI §Fixtures; SH §Station fixtures, §2c | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. `ItemAccessServer.Fixtures` is empty (`ItemAccessServer.cs:237`). Shops 2c, not started |
| OI-FIX-03 | **A fixture carried out of its place** (30 m from its shop's register) is taken from the hand and put back on its spot | OI §Fixtures; SH §2c; F13; Not built | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was needs decision. Decided 2026-10-21, never built (shops 2c). Shops gap 19 (walking off with the scanner) is open. Confirm, then build. Item-review triage: decided, not built. |
| OI-FIX-04 | **Leaving and saving put a fixture back:** its carrier leaving, and every save, return it to its spot; it's never saved as a possession, and an old save with one in an inventory puts it back at load | OI §Fixtures; SH §2c; F13 | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was needs decision. As OI-FIX-03. Item-review triage: decided, not built. |
| OI-FIX-05 | A fixture is never owned and never reaches Lost and Found | OI §Fixtures | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Waits for OI-FIX-02. A deleted car destroys the scanner today (OI-LF-10) |
| OI-FIX-06 | A player's own game refuses to put a fixture in the inventory, so the client never offers it | SH §2c | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Today the server's refusal drops it at the player's feet (#296) |

## Containers

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-CON-01 | A container is private to its owner: a deposit needs the depositor to hold and own the item, and a deposit never changes ownership | OQ §Decided; LC §Cold-container lifecycle | `[x]` | Pre-overhaul; the rule moved into `ItemAccess` (#296) · `deposit-refused` :52-63 (p+d), `containers` :59-60 (p only) |
| OI-CON-02 | A withdrawal takes its owner from the cold record's identity; if the withdrawer leaves as the item wakes, it aborts (`withdraw-actor-left`) and the item stays in the box | #304; BR LF6 | `[~] built, unproved` | #304 · `ContainerServer` (`withdraw-actor-left`). No scenario leaves mid-withdrawal |
| OI-CON-03 | A withdrawal that fails after registering the item moves the ledger back too | F12; container-storage.md Known issues | `[ ]` | The ledger moves before `CommitWithdrawal` (`ContainerServer.cs:391-424`). Item-review triage: real bug (cosmetic: heals at the next load). |
| OI-CON-04 | **A cold-container save the import rejects isn't overwritten** with an empty graph at the next save, and one bad record is quarantined instead of the whole graph | F18; container-storage.md Not built, Known issues | `[ ]` | Every stored item is lost (`ContainerServer.cs:59-66, 581-605`). Item-review triage: real bug (inventory loss: every stored item of every player). Item review's fix, branch `items/container-save-refused` (ea596377, no PR, scenario `item-bug-container-save-refused` p+d), is an input, not merged: the refactor's rows that stand alone replace it (R4, C1). |
| OI-CON-05 | Browse and deposit wait for the save's import, so a deposit made before it isn't wiped | F19; container-storage.md Not built | `[ ]` | `ContainerServer.cs:71-97`. Item-review triage: real bug (inventory loss; the window is about 1 s today). |
| OI-CON-06 | A failed retirement is retried, and saved as a cleanup obligation (`ColdCommittedPendingRetirement`) | LC §Cold-container lifecycle, §Implementation status; F17 | `[ ]` | Export writes every item `Cold` (`ContainerGraph.cs:542-556`). Item-review triage: real bug (a duplicate: a half-retired deposit is saved both Cold and as a possession). |
| OI-CON-07 | Retrying a committed withdrawal returns its result instead of throwing | F21 | `[ ]` | Latent: the client never reuses an operation id. Item-review triage: real bug (latent inventory loss if an operation id is reused). |
| OI-CON-08 | The holder of an unowned shell can open it | F22 | `[x]` | #331 · `ContainerServer.ActorOwnsPhysicalShell` (owner, or the holder of an unowned shell), `ResolveShellOwner` · `containers UNOWNED=1` (p; d: end pass) |
| OI-CON-09 | A restored shell never mints a new container id between its record and its state (0.25 s later) | F23 | `[ ]` | Unverified (`WorldItemPersistenceRestore.cs:221-232`). Item-review triage: real bug (a whole box orphaned in a narrow window; needs a game check). |
| OI-CON-10 | The container UI uses hidden proxy objects and `ColdProxyContainer`, which never become items the server keeps | F20 | `[ ]` | The UI uses hidden proxies and `ColdProxyContainer`. Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-CON-11 | A container's records are removed when the container is destroyed | container-storage.md Not built | `[ ]` | |
| OI-CON-12 | A deposit's Destroy and a withdrawal's Create respect interest | container-storage.md Known issues | `[ ]` | Sent to every player |
| OI-CON-13 | Moving an item between a parent and a child container isn't refused as stale | container-storage.md Known issues | `[ ]` | Sends the open container's revision as the source's |
| OI-CON-14 | Deltas and subscriptions, paging beyond 64, request rate limits and a cap on concurrent withdrawals | container-storage.md Not built | `paused: physics and wake fidelity` | Paused for the item refactor (D4, user 2026-10-23); was [ ] |
| OI-CON-15 | A deposit checks the destination's nesting depth, so no graph deeper than 8 is made (which the import then refuses, OI-CON-04) | Item-review's triage of F18 | `[ ]` | The deposit doesn't check it (`ContainerGraph.cs:641-702` against `ValidateDepthFrom` `:705-726`) |

## Persistence

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-PER-01 | The ledger is saved as `Multiplayer.OwnedItemsV1`, in the same pass as the stores, after a Lost and Found reconcile | OI §In the vanilla save; #282 | `[x]` | #282 · `LedgerSaveCodec.cs`, `ServerLedger.WriteSave` · `ledger-save` restart 1 (p+d); Protocol `LedgerSaveCodecTests`. The order after the Lost and Found reconcile isn't checked |
| OI-PER-02 | It's additive: a build without the ledger drops it, and the next ledger build rebuilds it by migration | OI §In the vanilla save | `not needed: fresh items (user 2026-10-23, D1)` | A format marker drops old item data, so a rebuild across builds doesn't apply. Was [x]: #282 · `ServerLedger.cs:169-172` (a save written without the key) · `ledger-save` :103-107, :135 (born `Migrated`, p+d) |
| OI-PER-03 | The load check runs once all stores have loaded, before anyone logs in | OI §Loading; #282 | `[x]` | #282 · `ServerLedger` load check · `ledger-save` :97 (p+d) |
| OI-PER-04 | An owned world item survives two restarts (kept by owner, not by `BelongsToPlayer`; the restore sets the flag again) | OI §Known bugs ("found in game"); #282 | `[x]` | #282 · `WorldItemPersistenceSave.ShouldPersist`, `WorldItemPersistenceRestore` · `ledger-save` ground item across two restarts (p+d) |
| OI-PER-05 | Load: an item in the store its `where` names is kept; in exactly one other store, that store wins and `where` is corrected and logged | OI §Loading | `[x]` | #282 · `LoadReconciler.ApplyToLedger` (Core, `:176-205`) · `ledger-save` (Keep, p+d); Core `LoadReconcilerTests` (Correct) |
| OI-PER-06 | **Load: an item in more than one store** keeps the copy where `where` says (else by priority: Lost and Found, container, installed, a possession, world); the others are destroyed and logged (`DropCopy`) | OI §Loading; F1; Not built | `[ ]` | Logged, not carried out ("shadow", `ServerLedger.cs:216-218`). Item-review triage: decided, not built. |
| OI-PER-07 | **Load: another player's item in a saved possession** goes to its owner's Lost and Found (`RecoverToLostAndFound`, `SaveRecovery`) | OI §Loading; F1; Not built | `[ ]` | Logged, not carried out. Item-review triage: decided, not built. |
| OI-PER-08 | Load: an ended entry's item found in a store lives again (`Revive`); an owned item with no entry is registered, born `Migrated`; an entry found nowhere is `Missing` | OI §Loading | `[x]` | #282 · `LoadReconciler.ApplyToLedger` · `ledger-save` :135 (Migrated, p+d); Core `LoadReconcilerTests` (Revive, MarkMissing) |
| OI-PER-09 | Lost and Found entries bind by the item's id at load, for every owner, online or not | OI §Loading; #282, #304 | `[x]` | #282, #304 · `LostAndFoundServer` load · `absent-owner` :60-67, `item-id-roundtrip` :73-76, `lost-and-found-many-owners` :60-67 (p+d) |
| OI-PER-10 | Every player's possessions, the host's included, are saved by identity in `WorldItemsV1`; the host's vanilla inventory is only a mirror, cleared at a multiplayer load | LC §Persistence ownership; F24 | `[ ]` | Still written (`NetworkedSaveGameManager.cs:106`). OI-MIG-04 reads it, so decide both together. Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-PER-11 | An unowned item in flight at save time isn't saved (no extra copy of a thrown spray can or office prop) | LC §Persistence ownership; F25 | `[ ]` | A lease open at save time saves it (`WorldItemPersistenceSave.cs:40-43`). Item-review triage: real bug (a duplicate). |
| OI-PER-12 | A train-interior item waits for its car at load, and goes to Lost and Found (`SaveRecovery`) if the car is gone | F27; item-world-sync.md Not built | `[ ]` | Restored at once at the saved pose. Item-review triage: decided, not built. |
| OI-PER-13 | A dynamic owned `Attached` item (a bought EOT lantern) is saved with its owner | F26 | `[ ]` | Not in `WorldItemsV1`, not managed. Unverified. Item-review triage: real bug (the owner is lost at reload; unverified). |
| OI-PER-14 | `ServerPlayer.Inventory` and the empty per-player `"inventory"` it writes are removed | OI §What goes away; F28 | `[ ]` | `NetworkedSaveGameManager.cs:91-95`. Item-review triage: decided, not built. |
| OI-PER-15 | A saved row that fails to restore once (a missing item mod, an authored key not found) isn't erased at the next save | BR SL5 | `[ ]` | Code only |
| OI-PER-16 | A scene-authored item in a player's inventory survives a second restart | BR SL3 | `[ ]` | Code only (`LiveItems.cs:147`) |
| OI-PER-17 | A dedicated host's save-reload doesn't gain items (one or two extra after the second load) | Map SR-+1; OI §Found in testing | `[ ]` | Unexplained; needs a diff step in `save-reload` |
| OI-PER-18 | After a load, a cab item isn't left in vanilla's Lost and Found list while its record says `TrainInterior` | BR D2 | `[ ]` | Dedicated host, from #282's runs |
| OI-PER-19 | An unowned item a player carries is saved with them and comes back with them | OI §Decisions ("Found items aren't yours"); LC §Persistence ownership | `[~] built, unproved` | `WorldItemPersistenceSave.cs:143-147` saves anything carried. No scenario. At a disconnect the hand item is dropped, and not saved if unowned (OI-JOIN-06, BR SL2) |
| OI-PER-20 | **One key, each fact once:** items are saved in `Multiplayer.Items`; each item's state once, its owner once (in the ledger); nothing else saves an owner, a slot or a state | [item-refactor.md](design/item-refactor.md) §Saving and loading | `[ ]` | The owner is saved in five places today (`WorldItemsV1`, the cold records, Lost and Found, `dvmpPersistentOwner`, `OwnedItemsV1`). R4 |
| OI-PER-21 | A save without `Multiplayer.Items` loads the world and the career and starts every player's items fresh; the old item keys are ignored, logged once and not written again | Refactor D1 (user, 2026-10-23) | `[ ]` | R4, scenario `old-save` |
| OI-PER-22 | Nothing managed is written to vanilla's `Storage_World` or `Storage_LostAndFound`, and multiplayer never reads vanilla's item lists; Lost and Found's items are saved only in the mod's key | Refactor D2 (user, 2026-10-23) | `[ ]` | Vanilla's Lost and Found storage holds those items' state today. R4. The host's inventory mirror is OI-PER-10 |
| OI-PER-23 | Saved rows stand alone: a row that won't load is kept and written back unchanged and never empties the others, and no player request is served until the load has run, so a cold-container save can't fail as a whole (F18 impossible by design) | Refactor C1 (user, 2026-10-23) | `[ ]` | R4, with OI-CON-04, -05, -15 |

## Projection and world sync

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-SYNC-01 | No client-made items: adoption is gone, and an unbound client item is either an authored projection kept dormant or destroyed | OI §5; LC §Client projection invariant; #307 | `[x]` | #307 · client `NetworkedItem` binding · `rejoin` :56 (p+d), `starting-items` |
| OI-SYNC-02 | `ClientAdoption` and `ReturnedToRejoiningOwner` stay only for wire numbering, and nothing produces them | F31 | `[ ]` | Kept for wire numbering, and `ClientAdoption` is still read (`LiveItems.cs:259, 371`). Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-SYNC-03 | The `Container` and `SnappedAttachment` placements are unused: a stored item has no record, and a snapped item is a world item | LC §Orthogonal state axes; F29 | `[ ]` | Nothing assigns either (`ItemAuthorityData.cs:5-18`). Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-SYNC-04 | **A static-parent item (on a shelf) stays in players' interest** after a spatial sample or commit | F42; item-world-sync.md Known issues | `[ ]` | `IsNonSpatialRelevantTo` has no `StaticParent` case. Unverified in game. Item-review triage: real bug (the item is invisible to clients until a reload). |
| OI-SYNC-05 | Every change to one of the server's copies re-indexes the item for interest (a cost, not a correctness rule) | F43 | `[ ]` | Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-SYNC-06 | Interest is entered at 3×3 cells and left outside 5×5 | F44 | `[ ]` | Code is 3×3/5×5. Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. |
| OI-SYNC-07 | A projection is known only once acknowledged: a failed projection sends a negative acknowledgement so the Create is re-sent, and a late retirement acknowledgement doesn't hold a re-entry back | F45; item-world-sync.md Known issues | `[ ]` | Item-review triage: real bug (cosmetic: re-entry blocked for 5 s). |
| OI-SYNC-08 | An authored key collision refuses only those entries, never every client's catalogue | F46 | `[ ]` | Item-review triage: decided, not built. |
| OI-SYNC-09 | `WorldItemStaticParentRegistry` doesn't rescan the scene on every miss, and a moved parent still resolves | F47 | `[ ]` | Item-review triage: doc-only. **Kept as the design said, user 2026-10-23** (a real gap). A performance note |
| OI-SYNC-10 | Unknown player location fails safe for cleanup: a player still loading protects slots and forks near them | LC §Cleanup and recovery precedence; F30 | `[ ]` | Replenishment and fork cleanup count only `ReadyForItems` players. Item-review triage: doc-only. **Kept as the design said, user 2026-10-23** (a real gap). |
| OI-SYNC-11 | A lower cleanup never erases a higher record: stock-fork cleanup never removes an owned, claimed, contained, Lost and Found or protected item | LC §Cleanup and recovery precedence, §Forbidden | `[~] built, unproved` | `OfficeStockReplenishment.cs:127-133` rejects such records. No test or scenario |
| OI-SYNC-12 | Office stock left in a parked cab isn't destroyed after 30 s | BR LF8 | `paused: not about loss` | Paused for the item refactor (D4, user 2026-10-23); was needs decision. By design today (`OfficeStockReplenishment.cs:26,127-133`). The bug review asks the user |
| OI-SYNC-13 | An office prop's reset checks interest where the prop is, not only at its baseline cell | item-world-sync.md Known issues | `[ ]` | |
| OI-SYNC-14 | A sync-completion barrier at join, not a fixed 0.25 s wait | item-world-sync.md Not built | `paused: physics and wake fidelity` | Paused for the item refactor (D4, user 2026-10-23); was [ ] |
| OI-SYNC-15 | Relays respect interest | item-world-sync.md Not built | `paused: physics and wake fidelity` | Paused for the item refactor (D4, user 2026-10-23); was [ ] |
| OI-SYNC-16 | Compressed or chunked bulk updates | item-world-sync.md Not built | `paused: physics and wake fidelity` | Paused for the item refactor (D4, user 2026-10-23); was [ ] |
| OI-SYNC-17 | Per-recipient projection state in the item debug snapshot | LC §Implementation status, §Required lifecycle observability | `[ ]` | |
| OI-SYNC-18 | Every move and removal has a recorded cause, logged on the server and sent with the update to clients | LC §Why an item changed | `[~] built, unproved` | `ItemChangeLog`, `ClientItemChangeLog`; Core `ItemChangeReasonsTests` checks the wire values only. No scenario checks a recorded cause |
| OI-SYNC-19 | **One request, one result:** every item request carries a per-client `RequestId`, the item (NetId or handle) and its expected revision; one result answers it (accepted, or a refusal code and detail, and the revision); a repeated `RequestId` gets the same answer and changes nothing | [item-refactor.md](design/item-refactor.md) §The wire | `[ ]` | Seven request flows correlate and refuse differently today. R2 |
| OI-SYNC-20 | **One update per recipient:** an item update names a holder by session number and never an owner; it tells each player its relation to the item, and only the owner the claim | [item-refactor.md](design/item-refactor.md) §The wire; refactor D3 | `[ ]` | R2 |

## Physics and train wake

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-PHY-01 | Only a reliable, accepted settlement commits the durable pose; a motion sample never changes placement, holder or owner | LC §Canonical lifecycle, §Placement and motion | `[~] built, unproved` | `item-release` (p+d) proves settlements land and commit. No test that a motion sample can't change placement, holder or owner |
| OI-PHY-02 | Pickup, containment, Lost and Found, installing and destroying end any older lease explicitly (not only by the revision advance) | LC §Placement and motion, §Implementation status; item-spatial-physics.md Not built | `[ ]` | See also OI-ESS-11 (recall) |
| OI-PHY-03 | A lease whose placement changed without a transition (a Lost and Found collection mid-flight) ends, instead of failing every frame | F40 | `[ ]` | Item-review triage: real bug (a duplicate). |
| OI-PHY-04 | A server-simulated lease whose settlement is refused ends | F39 | `[ ]` | Item-review triage: real bug (it feeds OI-PER-11 and OI-PHY-03). |
| OI-PHY-05 | A parent change during a flight is committed as one | F33 | `[ ]` | Item-review triage: doc-only. **Kept as the design said, user 2026-10-23** (a real gap). |
| OI-PHY-06 | **A settled train item stays kinematic** on a player who gets it later (a Create) or applies a convergence correction | F34; item-spatial-physics.md, train-item-wake.md Known issues | `[ ]` | `HandleDroppedOrThrownState` makes it dynamic. Unverified in game. Item-review triage: real bug (misplaced on the client; unverified). |
| OI-PHY-07 | A dedicated host can take a transferred lease and simulate a train wake | F32 | `[ ]` | Candidates are players only. Item-review triage: real bug (cosmetic: on a dedicated host a thrown item freezes mid-air if its thrower leaves). |
| OI-PHY-08 | A pickup range check on the server | F35; item-world-sync.md Not built | `[ ]` | Item-review triage: decided, not built. |
| OI-PHY-09 | A throw sends its direction only, and the server simulates the flight from its own copy | F36 | `[ ]` | Only `ThrowDirection`. Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. In the refactor (R8): D4 as approved by the user left it in; D6 rewrote it to the code's rule. |
| OI-PHY-10 | Observers extrapolate one target for up to 0.15 s and smooth towards it at 10/s | F37 | `[ ]` | They extrapolate up to 0.15 s and smooth. Item-review triage: doc-only. **Rewritten to the code's rule, user 2026-10-23**; ticked once the new doc and a scenario or test prove it. In the refactor (R8): D4 as approved by the user left it in; D6 rewrote it to the code's rule. |
| OI-PHY-11 | Fall rescue commits the rescue (revision and spatial commit), and covers non-`World` items | F38 | `[ ]` | Item-review triage: doc-only. **Kept as the design said, user 2026-10-23** (a real gap). Open: a train-interior item that falls under the map |
| OI-PHY-12 | A wake's simulator is given interest in the items, so a late joiner can witness impacts; car motion is measured in the car's frame, so curves don't count | F41; train-item-wake.md Not built, Known issues | `[ ]` | Item-review triage: doc-only. **Kept as the design said, user 2026-10-23** (a real gap). |
| OI-PHY-13 | A scenario for train-item wakes (the Train Item Lab is gone) | train-item-wake.md Not built | `paused: physics and wake fidelity` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. `train-items` checks only that an item stays in a moving cab |
| OI-PHY-14 | Throwing onto a moving flatcar lands within tolerance on both games | #148; OI §Plan ("not the ledger's") | `paused: physics and wake fidelity` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. `item-release-throw-flatcar` flakes on both host kinds |
| OI-PHY-15 | Recovery for runaway or never-sleeping motion | item-spatial-physics.md Not built | `paused: physics and wake fidelity` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Needs research first |

## Vanilla paths

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-VAN-01 | Vanilla's `CombineAndResolveIllegalDuplicates` doesn't run in multiplayer, so it can't drop a player's radio or wallet from Lost and Found at load | [item-refactor.md](design/item-refactor.md) §Every vanilla path (found 2026-10-23, from the code) | `[ ]` | Still runs on the host: `StartingItemsControllerPatch.cs:17-23` turns off only the two safeguards. R6, scenario `duplicate-essentials-load` |
| OI-VAN-02 | **Binning:** an owned item put in a bin or dumpster is destroyed as in vanilla and its entry ends `Discarded`, by the player who binned it; an essential is issued again at its owner's next join; only your own or unowned items can be binned | Refactor D10 (user, 2026-10-23); [item-refactor.md](design/item-refactor.md) §Every vanilla path (found 2026-10-23, from the code) | `[ ]` | Today the destroy is suppressed and Lost and Found refuses the item (no longer `BelongsToPlayer`): it lies in the dumpster for ever and its entry never ends. R6, scenario `bin-owned` |
| OI-VAN-03 | A consumable used up is one server step, `UseUp(item, replacement)`: the used item is destroyed on every game, the replacement made by the server and born `Replaced` (`RecordReplacement`); the spray can and the garage key use it | [item-refactor.md](design/item-refactor.md) §Every vanilla path | `[ ]` | Duct tape, solder and labels run on the client only; the sprayer isn't handled. R6, scenario `use-up`. The gadget tools are OI-LIN-02 to -04 (paused) |
| OI-VAN-04 | The dev console's spawns and destroys go through the server (a spawn unowned, a destroy ending the entry with the cause `DevConsole`); on a client they are refused with a message | [item-refactor.md](design/item-refactor.md) §Every vanilla path | `[ ]` | Not patched; a client's spawn vanishes. R6 |
| OI-VAN-05 | `RespawnOnDrop` never puts an item in vanilla's Lost and Found list in multiplayer; only authored stock's reset runs, as a server step | [item-refactor.md](design/item-refactor.md) §Every vanilla path | `[ ]` | An unowned `BelongsToPlayer` item can sit in vanilla's list while its record says World. R6 |
| OI-VAN-06 | Vanilla's other `AddItemToLostAndFound` callers are blocked for every item; a gadget whose anchor is missing at load goes to its owner's Lost and Found | [item-refactor.md](design/item-refactor.md) §Every vanilla path | `[ ]` | Suppressed for owned items only (`StorageControllerPatch.cs:71-89`). R4 (OI-LF-22), R6 |

## Layout and names

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-LAY-01 | Item code is in one folder per subsystem inside each side's `Items` folder, namespaces match the folders, tests mirror them, and code and docs use the three names (ledger, live state, saving and loading) | [item-refactor.md](design/item-refactor.md) §Code layout; the user, 2026-10-23 | `[ ]` | R0 moves and renames; ticks when R8's docs match |

## Shops seam

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-SHOP-01 | Every purchase, the host's own player's included, is made by the server and owned by whoever scanned the line before anyone sees it | OI §What an owned item is, §5; SH §Owned items | `[x]` | Shops #284 · `ShopDelivery.Spawn` · `shop` :65 (p+d), `shop WHO=host` (p: the host's own player). The ledger's `Purchased` birth isn't checked (OI-LIN-07). Its workstream is paused for the item refactor (D4); it stays done |
| OI-SHOP-02 | A purchase is born `Purchased` with the shop, the transaction id and the payer in its detail | SH §Owned items (seam 1); F48 | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Shops PR 3. No transaction id or payer today (`ShopDelivery.cs:20-51`). Item-review triage: decided, not built. |
| OI-SHOP-03 | A purchase whose owner is over 200 m away or offline when it comes due is born in their Lost and Found (`PurchasedDeliveryFallback`) | OI §Lost and Found ("Reasons"); SH §Owned items (seam 2); F48 | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Shops PR 3. Always on the counter today. Item-review triage: decided, not built. |
| OI-SHOP-04 | A line whose owner has left is delivered to their Lost and Found, not unowned | SH §Owned items; F48 | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Shops PR 3. Item-review triage: decided, not built. |
| OI-SHOP-05 | `shopStocked` mirrors vanilla's `Restock`: set at birth, cleared where vanilla clears it (a used garage key), read at load and migration, never set on a replacement | OI §What other systems read; SH §Owned items (seam 3) | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [~] built, unproved. Set at birth from the item's flag (`ServerLedger.cs:340, 435-437`). No scenario. Clearing and loading are OI-SHOP-10 |
| OI-SHOP-06 | Per-player stock is `CountLive(owner, prefab, shopStocked)`, and the ledger's change event sends that player's allowance | OI §What other systems read; SH §Per-player stock, §6 | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Core API built and tested (#278); nothing outside Core uses it. Shops PR 6 |
| OI-SHOP-07 | Installed gadgets count against their owner's stock | SH §Gadgets; OI §What other systems read | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Shops PR 6 |
| OI-SHOP-08 | Receipts are server-made, unowned world items | SH §Receipts, §Owned items (seam 5); OI §5 | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Shops PR 3. Client-local today |
| OI-SHOP-09 | A wallet coin flick goes through the server; it can't credit money from nothing | Map COIN; OI §Found in testing; #307 | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Since #307 the flicked coin vanishes on the client. Not tried in game |
| OI-SHOP-10 | `shopStocked` is cleared where vanilla clears `Restock` (a used garage key), and read from each store at load and migration, cold containers and Lost and Found included | OI §What other systems read; SH §Owned items (seam 3) | `paused: shops` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Never cleared on the server (`SetShopStocked` has no callers outside Core). Cold-container and Lost and Found hits migrate as false |

## Gadgets seam

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-GAD-01 | An installed gadget is an `Installed` item, and its owner is saved in its vanilla save data (`dvmpPersistentOwner`) | GD §The installed gadget and its item, §Persistence, §1 | `[x]` | Gadget #281 · `ItemIdentityPatch` (`dvmpPersistentOwner`) · `gadget-save` :47-83 (p+d) |
| OI-GAD-02 | The ledger counts installed gadgets as a store, and migration reads `dvmpPersistentOwner` | OI §Plan (meeting points); OI §Migration | `[~] built, unproved` | The load check reads installed gadgets, and migration their saved owner. `ledger-save` has no gadget; `gadget-save` checks records, not the ledger |
| OI-GAD-03 | An item's id and owner survive install, removal, knock-off and car deletion | GD §Owned items | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [~] #314. Gadget 3a, draft |
| OI-GAD-04 | The `Installed` place records the gadget's UID | OI §The ledger (`where`); F10; Not built | `[ ]` | `WhereOf` writes 0 (`ServerLedger.cs:318`). Item-review triage: decided, not built. |
| OI-GAD-05 | A deleted car sends its owned gadgets to their owners' Lost and Found at once, online or not; unowned ones stay in the world | GD §The installed gadget and its item, §Forced removals | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [~] #314. 3a collects them with `GadgetDestinationMissing` (`GadgetServer.cs:141`), not a car-deleted reason |
| OI-GAD-06 | The item code never destroys an installed item, on any game, and logs a refused destroy | GD §The installed gadget and its item; F49 | `[ ]` | Item-review triage: doc-only. **Kept as the design said, user 2026-10-23** (a real gap). gadgets.md is being rewritten by #314-#320 In the refactor (R7): D4 as approved by the user left it in, and D6 keeps the design's rule. |
| OI-GAD-07 | The gadget server's install and removal checks call `ItemAccess` | OI §Permissions | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ]. Not on #314's branch |
| OI-GAD-08 | Old installed gadgets with no owner (saves from before gadget PR 1) | OI §Open decisions | `paused: not about loss` | Paused for the item refactor (D4, user 2026-10-23); was needs decision. Recommended: unowned, anyone may remove them, rather than the career owner's |
| OI-GAD-09 | Aiming at an installed gadget shows its owner's name, also while they're offline | GD §The installed gadget and its item | `paused: gadgets` | Paused for the item refactor (D4, user 2026-10-23); was [ ] |
| OI-GAD-10 | **An `Installed` home names a car or the world** (vanilla's `WorldCustomization`, key `:global:`): a world-installed gadget is an owned item like a car's, saved and restored at its pose, and never sent to Lost and Found for a missing anchor | User correction 2026-10-23 (G5: world installs, which some mods need; custom_item_mod builds its gadgets with no car requirement) | `[ ]` | R3. `WhereOf` and the save know only a car today |
| OI-GAD-11 | Installing on and removing from the world works in multiplayer like on a car (preview, install, removal and its sync); the player house, the Lost and Found shed and the paint station stay refused | User correction 2026-10-23 (G5) | `paused: gadgets` | The legacy gadget design refuses every non-car anchor (gadgets.md, Off-train installs) |

## Mod items

Items other mods add, through custom_item_mod and its item packs (refactor D11). Paused by the user
2026-10-23, for after the refactor.
Proving these needs an item pack in the test install.

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-MOD-01 | An item whose prefab comes from a mod (custom_item_mod's `custom_item_mod/<name>/prefab`, resolved by its `Resources.Load` patch) is spawned, saved and restored by the server and built on every client by the same prefab name, like a vanilla item; a name a game can't resolve is logged and kept `Unloadable` (D9), never dropped | Refactor D11 (user, 2026-10-23) | `paused: mod items` | Paused by the user 2026-10-23 (later, after the refactor). **Broken today:** both prefab lookups read only vanilla's `Globals.G.Items.items`, which custom_item_mod never adds to (it adds shop data and patches `Resources.Load`). So a client can't build a custom item another game has (`ClientItemManager.Compatibility.cs:33` → `NetworkedItemManager.TryGetItemPrefab`), and the server can't restore a saved one (`WorldItemPersistenceRestore.cs:127`), which then erases it at the next save (SL5). The lookup has to fall back to `Resources.Load(prefabName)`, as vanilla's own load does. Untested with any mod item |
| OI-MOD-02 | **Each item's own saved state reaches clients:** its `ItemSaveData` JSON (where mods keep their state) goes with the item when a client first sees it and again when the server's copy changes, applied through `ItemSaveDataLoaded`; the server's copy is the truth, and a client's goes up with the request that changes the item | Refactor D11 (user, 2026-10-23) | `paused: mod items` | Paused by the user 2026-10-23 (later, after the refactor). Today item state is saved on the server only |
| OI-MOD-03 | **The same item mods on join:** a client missing custom_item_mod or an item pack the server has is refused, with the mod named; custom_item_mod is registered `All` in `ModCompatibilityManager` | Refactor D11 (user, 2026-10-23) | `paused: mod items` | Paused by the user 2026-10-23 (later, after the refactor). `ModCompatibilityManager.ValidateClientMods` already compares undefined mods; no registration, no scenario |
| OI-MOD-04 | custom_item_mod's shop items are bought through the server like vanilla stock | Refactor D11 | `paused: shops` | |
| OI-MOD-05 | custom_item_mod's gadgets (its `GadgetBase` map) install, sync and save like vanilla gadgets, on a car or the world | Refactor D11; G5 | `paused: gadgets` | |

## Migration

| ID | Requirement | Source | Status | Proof, or what's missing |
| --- | --- | --- | --- | --- |
| OI-MIG-01 | A save without a ledger gets one built from the stores (`WorldItemsV1` with an owner identity, Lost and Found, cold containers, installed gadgets) | OI §Migration | `not needed: fresh items (user 2026-10-23)` | Old saves start every player's items fresh (refactor D1); was [~] built, unproved. Proved for `WorldItemsV1` and Lost and Found (`ledger-save` :103-107, :135, p+d). Cold containers and installed gadgets aren't in the scenario; they also migrate with `shopStocked` false |
| OI-MIG-02 | Migration is deterministic: running it twice gives the same ledger | OI §Migration | `not needed: fresh items (user 2026-10-23)` | Old saves start every player's items fresh (refactor D1); was [x]. #278 · `LoadReconciler.cs` · Core `LoadReconcilerTests` |
| OI-MIG-03 | A `WorldItemsV1` record with only an owner number (from before identities) is unowned, and counted in the log | OI §Migration | `not needed: fresh items (user 2026-10-23)` | Old saves start every player's items fresh (refactor D1); was [ ]. Treated as unowned (`SavedItemRow.FromJson` reads no owner number), but not counted or logged |
| OI-MIG-04 | Items lost to the cold-withdrawal bug are recovered from the host's vanilla inventory mirror (`SaveRecovery`), after checking the mirror really holds them | OI §Migration; F11; Not built | `not needed: fresh items (user 2026-10-23)` | Old saves start every player's items fresh (refactor D1); was [ ]. No code. See OI-PER-10. Item-review triage: decided, not built. |
| OI-MIG-05 | A `WorldItemsV1` record with an empty id takes the id from its state, or is re-minted | OI §Migration; F11; Not built | `not needed: fresh items (user 2026-10-23)` | Old saves start every player's items fresh (refactor D1); was [ ]. No code. Item-review triage: decided, not built. Empty-id records are silently dropped (`WorldItemPersistenceRestore.cs:123-125`) |
| OI-MIG-06 | Every identity with a `Players` entry is marked as having had its starter kit | OI §Migration; #306 | `not needed: fresh items (user 2026-10-23)` | Old saves start every player's items fresh (refactor D1); was [~] built, unproved. `ServerLedger.cs:196-200`. No test or scenario (`starting-items` forgets the marker; it doesn't migrate) |
| OI-MIG-07 | Fixture tests of migration in `Multiplayer.Protocol.Tests`: every store's owned items come out once with the right owner and `where` | OI §1b | `not needed: fresh items (user 2026-10-23)` | Old saves start every player's items fresh (refactor D1); was [ ]. None. Migration is tested as a plan in Core (`LoadReconcilerTests`) |
