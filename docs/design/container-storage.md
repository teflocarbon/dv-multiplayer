# Cold container storage

> Status: as built, checked against the code on 2026-10-23 (`items/lineage`). Built 2026-07 (#12),
> with one id for life since owned-items PR 1b (#282). In game, only the `containers` and
> `deposit-refused` scenarios cover it: nesting, cycles, moves, save and reload, rejoin and the
> limits are unit-tested only.

An item container (anything with an `AItemContainer`, such as a toolbox) is an ordinary networked
item: the **shell**. Its contents aren't Unity objects. They are records in one server-side graph,
`NetworkedColdContainerManager.Graph` (`ColdContainerGraph`, `Multiplayer.Core/Containers/`), saved
with the career. A client never gets the contents as items: it asks for a page of rows when its
player opens the container. Contents are private to the container's owner.

## Records

- **`ColdContainerRecord`**: the container id (a GUID), the owner's identity, prefab, capacity,
  revision, a session handle (not saved), and `ParentItemId` when the container is itself stored in
  another one.
- **`ColdStoredItemRecord`**: the item's own id ([owned-items.md](owned-items.md#one-id-for-life)),
  prefab, display name, owner identity, authored key, parent container and slot, the child container
  when the item is itself a container, and `DetachedState`: the item's `ItemSaveData` JSON, which
  carries its id again. Plus a lifecycle state and a session handle, neither saved.
- **Lifecycle** (`ColdItemLifecycleState`): `Cold`, `ColdCommittedPendingRetirement`,
  `PreparingWithdrawal`, `MaterializedPendingCommit`, `Materialized`.
- **Limits** (`ColdContainerLimits`): depth 8, 256 nodes visited per check, 4096 items per owner,
  capacity at most 256, detached state at most 512 KiB, 64 rows a browse. The last 4096 completed
  operation results are kept, so retrying an operation id inside the graph returns its result.

A stored item has no registry record and no NetId. Session handles, not GUIDs, go to clients.

## The shell's id

`ColdContainerIdentityPatch` gives every `AItemContainer` a `ColdContainerIdentity` at `Awake`, on
every game. Its GUID is minted the first time it is read, and travels in the shell's own vanilla save
data (`multiplayerColdContainerId`), so it follows the shell through the world, inventories, Lost and
Found and other containers. It is a different id from the shell item's own.

Every 24 ticks the server binds live shells (`BindPhysicalShells`), and a shell opened before that is
bound on demand. The container's owner is the shell's owner, else whoever holds it; a shell with
neither (unowned, lying in the world) isn't bound. The owner never changes once bound. Records are
never removed: a destroyed shell's container and contents stay in the graph and the save.

## Browse

`ServerboundContainerBrowsePacket`, by the shell's NetId or (nested) by handle. By NetId, the shell
must be bound and the requester its owner by number. The graph checks the requester's identity is the
container's owner (`container-private`), then returns the direct children by slot: handle, prefab,
display name, a foreign-owned flag, and a child container's handle and item count
(`ClientboundContainerViewPacket`). There is no distance or possession check: only ownership.

## Deposit

`ServerboundContainerMutationPacket`, kind `Deposit` (`NetworkedColdContainerManager.Deposit`):

1. The destination (by handle, else the shell) and the item. A shell named must be the requester's.
2. The requester must hold the item. A host's own player whose DV inventory has it while the record
   doesn't is reconciled first (`HostCorrection`).
3. `ItemAccess` (`Deposit`): never a fixture or an essential (`essential-not-in-containers`), and only
   the requester's own item (`deposit-item-not-owned`) ([owned-items.md](owned-items.md#permissions)).
   So a container only ever holds its owner's items.
4. Into the opened root shell, the live `AItemContainer.ValidItem` must accept it; otherwise the
   prefab's (`HostRuntimeCompatibility`), cached per prefab pair.
5. A container being deposited gets its own container record first.
6. The record uses the item's own id. `Graph.CommitDeposit` checks revision, slot, compatibility,
   cycles and depth, owner, duplicate id, an already parented child, and quotas, then holds the record
   as `ColdCommittedPendingRetirement`.
7. The live item is retired at once: a Destroy (`DepositedInContainer`) to every `ReadyForItems`
   player, dropped from every player's sets, its inventory and container membership revoked,
   `Object.Destroy`, then `Cold`, and the ledger moves the entry to the container. No tombstone is
   written and no shop restocks (`ShopRestockerRetirementPatch`).

If retirement throws, the item is made inactive and left, and the record stays committed. Nothing
retries it.

## Withdraw

`Withdraw` (a coroutine), to the inventory slot the client names:

1. `Graph.PrepareWithdrawal`: owner only, the expected revision, the item `Cold` → `PreparingWithdrawal`.
2. The prefab is made at DV's safety position, `BelongsToPlayer` set, a valid authored key re-adopted.
3. A frame later (as DV does), unless the requester has left (`withdraw-actor-left`): the detached
   state is loaded, which restores the item's id and a nested shell's container id.
4. `RegisterMaterializedColdItem`: a record in the requester's inventory, the owner from the stored
   identity (not a number worked out earlier), the item's own id (re-minted only if a live item has
   it), cause `WithdrawnFromContainer`.
5. `Graph.CommitWithdrawal`: the record leaves the graph. The Create goes to every `ReadyForItems`
   player, known for all.

Any failure puts the record back to `Cold` and destroys the staged object.

## Move

Cold to cold, by handles (`Move`): owner only on both containers, both revisions, slot,
compatibility, cycles and depth. Logged as `Cold item moved: ...`, `ItemMoveReason.Moved`.

## The client's UI

`ColdContainerUiIntegration` and `ColdContainerUiPatch` (`Multiplayer/Client/Items/`). Opening a
container (`ItemContainer.ToggleContainerAccess`) browses it, and the reply replaces the active
`ItemContainerProvider` model with a row per slot. Each occupied row is a hidden `GameObject` with a
`ColdContainerItemProxy` (an `IInventoryItemSpec`, not an item prefab); a container row also has a
`ColdProxyContainer` whose access browses the child. The inventory's move, add, remove and swap calls
are routed to the server while that container is open. Every mutation has a fresh operation id, and
every result is followed by a full browse (no deltas). An item another player owns is styled red
(`InventoryOwnershipUiPatch`). The host's own player goes through its local session with the same
packets (`LocalPlayerSession.Items`).

## Lost and Found

A shell collected into Lost and Found goes as one entry and keeps its graph; retrieving it doesn't
materialize its contents ([lost-and-found.md](lost-and-found.md)). Retrieving an item that is in a
container from the owned-items list sends the outermost container that isn't with its owner
([owned-items.md](owned-items.md#retrieval)).

## Saving

`Multiplayer.ColdContainers`: base64 of `ColdContainerSnapshotCodec` version 3 (magic `DVCC`). Export
writes every stored item as `Cold`, so a committed retirement isn't saved as one. Bounds: 16 MiB
encoded, 512 KiB state, 4096-byte strings, 65535 entries. Import runs once vanilla's items have
loaded, and `ImportSnapshot` checks the whole graph, all or nothing. `InstallGuidRekey` re-keys
owners in the graph and in a saved blob.

A save the server can't import (it doesn't decode, or `ImportSnapshot` refuses it) is kept: its text is
written back as it was at every save, every container operation is refused with
`container-save-unreadable`, and an error is logged. A build that can read it gets everything back
(item-bug-review.md, F18; scenario `item-bug-container-save-refused`).

## Not built

- Deltas and subscriptions (clients re-browse), paging (the client always asks for the first 64).
- A retry for a failed retirement; saving it as a cleanup obligation.
- Removing records when a container is destroyed.
- Request rate limits and a cap on concurrent withdrawals.
- Quarantining one bad record at load, instead of rejecting the save.
- Waiting for the save's import before browse and deposit are allowed.

## Known issues

From the code, not run in game (2026-10-23). The bug review tracks them.

- A deposit made before the import is wiped when the import replaces the graph.
- Moving an item between a parent and a child container from the UI sends the open container's
  revision as the source's, so it is likely refused as stale.
- A retried withdrawal (an operation id used twice) throws: the cached result has no record. The
  client never reuses one.
- If a withdrawal fails after `RegisterMaterializedColdItem`, the graph goes back to `Cold` but the
  ledger has already moved the entry to the player.
- A restored shell's state loads 0.25 s after its record; a shell scan in between may mint a new
  container id and save an empty container for ever. Unverified.
- The deposit's Destroy and the withdrawal's Create go to every player, interest ignored.
- An unowned shell is bound to whoever holds it, but browsing by shell needs the owner, so that holder
  can't open it.

## Code and tests

| Where | What |
| --- | --- |
| `Multiplayer.Core/Containers/ColdContainerGraph.cs`, `ColdContainerSnapshotCodec.cs` | The graph and its save |
| `Multiplayer/Server/Items/NetworkedColdContainerManager.cs` | Binding, browse, deposit, withdraw, move, save |
| `Multiplayer/Server/Items/ColdContainerIdentity*.cs` | The shell's container id |
| `Multiplayer/Client/Items/ColdContainerUi*.cs` | The client's UI bridge |
| `Multiplayer.Core/Items/StorageTransitionPlanner.cs`, `Multiplayer/Shared/Items/StorageIntegration.cs` | DV's storage lists made to agree: an item in exactly the list its placement names |
| Tests | `Multiplayer.Core.Tests/Containers/`; scenarios `containers`, `deposit-refused` ([testing.md](../testing.md)) |
