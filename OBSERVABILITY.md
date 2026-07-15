# Multiplayer observability system

This document describes the observability features currently implemented in the Derail Valley multiplayer mod. It is an implementation reference, not the original design proposal.

The system is deliberately additive. It observes the existing transport, packet handlers, validation branches, Unity state application, entity lifecycle, and world lifecycle. It does not replace packet subscriptions or processors, alter packet schemas, reorder handlers, change delivery methods, or change validation results.

## Feature status

| Area | Current status |
| --- | --- |
| Structured event contracts | Implemented, schema version 1 |
| Unique per-process sessions and JSONL logs | Implemented |
| Unique per-process human-readable mod logs | Implemented |
| Random loopback HTTP/SSE server | Implemented |
| Session discovery and heartbeat | Implemented |
| Shared embedded Web UI | Implemented |
| Combined host/client dashboard | Implemented |
| Generic packet receive/send summaries | Implemented |
| Item handler, validation, application, and lifecycle tracing | Implemented |
| Inventory, storage, container, and distance-respawn tracing | Implemented with exact game events and additive diagnostics |
| Player tracking and car-attachment tracing | Implemented |
| Train physics, control, port, and authority tracing | Implemented for the targeted paths |
| Origin-shift and scene tracing | Implemented |
| Debug entity registry and timelines | Implemented |
| In-game uGUI inspector | Implemented |
| Searchable in-game packet inspector | Implemented with sampling, suppression, and bounded history |
| Replication-flow inspector | Implemented for selected items using their retained structured timeline |
| Host item interest/known matrix | Implemented for selected items |
| Queue and unresolved-dependency health | Implemented for tick queues and item application dependencies |
| Network-ID integrity auditor | Implemented for item IDs, lookup mismatches, duplicates, and stale aliases |
| Tick, latency, jitter, and traffic monitor | Implemented |
| Observability self-profiler | Implemented |
| Automatic diagnostic capture triggers | Implemented, opt-in and bounded |
| Gaze selection and keyboard navigation | Implemented |
| Clipboard export for entity, timeline, event, and diagnostic bundle | Implemented |
| In-overlay display and world-label settings panel | Implemented |
| Item, player, and train world labels | Implemented |
| Live overlay/label state refresh | Implemented with bounded sampling |
| Markers and capture files with pre-roll | Implemented |
| Item desync detector | Initial implementation |
| Player and train desync detectors | Not yet implemented |
| Exhaustive decoded packet catalogue in-process | Intentionally remains in the standalone debug client |

## Enabling the system

Debug builds enable the system by default. Release builds default to disabled. It can be changed from the advanced multiplayer settings while the game is running.

When the system is disabled, it does not create the runtime GameObject, event store, file worker, discovery file, HTTP listener, entity reconciliation work, overlay, or labels.

The advanced settings are:

| Setting | Default | Purpose |
| --- | --- | --- |
| Enable Debug System | On in `DEBUG`; off in release | Starts or stops the entire in-process runtime |
| Enable Debug File Logging | On when debug is enabled | Writes the authoritative JSONL event log |
| Enable Debug Firehose | On | Starts the random-port loopback HTTP/SSE server |
| Raw Packet Capture | Off | Adds raw Base64 payloads where permitted and selects Raw trace mode |
| Enable World Labels | Off | Shows labels over supported network entities |
| World Label Radius | 30 m | Limits label candidates; configurable from 5–500 m |
| World Label Scale | 1.0× | Scales the complete world-space label from 0.5–3.0× |
| World Label Update Rate | 4 Hz | Controls label/state refresh from 1–10 Hz |
| Maximum World Labels | 64 | Caps normal labels from 1–128; verbose labels remain capped at 16 |
| Debug Overlay UI Scale | 1.0× | Scales overlay controls and text from 0.75–1.5× |
| Show Labels Through Walls | Off | Disables the normal occlusion check |
| High Frequency Sampling | 1 in 10 | Samples player tracking, train physics, ports, ping, and similar traffic |
| Max Entity Timeline Events | 250 | Bounded history retained for each entity |
| Max In-Memory Events | 10,000 | Capacity of the process-wide event ring |

File logging, the firehose server, label state, sampling, and memory capacities can be updated without restarting the game. Disabling the complete debug system tears down the runtime.

The older **Enable Debug Loopback Client** and **Debug Loopback Port** settings belong to the standalone multiplayer peer/debug client. They are separate from the in-process observability firehose.

## Sessions and files

Each game process creates a unique session ID:

```text
yyyyMMdd-HHmmss-p{processId}-{four-character-random-suffix}
```

The session begins with the role `idle`, changes to `host` or `client` when networking starts, and receives the local player name and ID after login. Session metadata also includes:

- schema version;
- process ID and verified process start time;
- session start and heartbeat times;
- role, player name, and player ID;
- random firehose port and URL;
- a random API token for mutating HTTP requests;
- log path;
- game build;
- repository commit metadata, or `unknown` when unavailable.

Files are stored below the installed mod directory:

```text
Multiplayer.Debug/
  multiplayer-{sessionId}.log
  dvmp-debug-{sessionId}.jsonl
  sessions/
    {sessionId}.json
  captures/
    {captureName}-{role}-{sessionId}.jsonl
```

The human-readable `multiplayer-{sessionId}.log` is controlled by **Enable Log File**. It uses the same session ID as the structured observability JSONL, so host and client logs never share a filename and the two log formats from one process can be paired directly. The mod no longer deletes or appends to the legacy shared `multiplayer.log` file.

Discovery records are written atomically and refreshed every two seconds. They are removed during a clean shutdown. Consumers reject a session when:

- its PID no longer exists;
- its process start time does not match, which protects against PID reuse;
- its heartbeat is more than ten seconds old.

## Structured events

All runtime, standalone-client, and dashboard events use the shared `Multiplayer.DebugProtocol` contracts.

Every event contains:

- schema version and session-local sequence;
- UTC timestamp, Unity frame, and optional network tick;
- session ID, PID, role, runtime side, and local player ID;
- category, event name, and severity;
- entity type and entity ID;
- optional correlation and causation IDs;
- a high-frequency flag;
- detached JSON-safe data.

The stable cross-process event key is:

```text
{sessionId}:{sequence}
```

Reusable packet objects and Unity values are detached at the call site into primitives, arrays, dictionaries, and vector/quaternion-shaped data. The background sinks never retain live Unity references or reusable packet instances.

Payloads also receive an inexpensive FNV-1a fingerprint so the same unchanged bytes can be compared across processes without retaining raw data.

### Event flow

```text
existing multiplayer or Unity boundary
  -> guarded DebugTrace / DebugRuntime call
  -> bounded DebugEventStore
     -> per-entity timeline
     -> asynchronous JSONL writer
     -> SSE subscribers
     -> active capture writer
```

The main event categories currently include:

- `session`
- `packet`
- `item`
- `player`
- `train`
- `world`
- `scene`
- `desync`
- `marker`
- `capture`

## Packet visibility

### Generic traffic summaries

`NetworkManager.OnNetworkReceive` observes the unread payload before the existing packet processor consumes it. It records direction, peer, channel, delivery method, byte length, and payload fingerprint.

The leading LiteNetLib packet hash is matched against the generated protocol manifest. This identifies packet types without decoding the same packet twice or changing subscriptions.

Existing serialization and send methods have guarded calls after packet writing and immediately before peer sends. Server broadcast loops using prebuilt writers also expose their send stage.

The normal generic stages are:

```text
packet.raw.receive
packet.serialized
packet.raw.send
packet.handler.before
packet.handler.after
packet.decoded
```

`packet.decoded` is produced for explicitly instrumented handlers when trace mode is `Decoded` or `Raw`. Summary mode avoids decoded projection work.

### Schema-aware packet projection

Decoded events represent the fields written by the packet's serialization schema rather than every public runtime property on the reusable C# object. `DebugPacketProjectorRegistry` provides explicit adapters for `CommonItemUpdatePacket`, `CommonItemsBulkUpdatePacket`, and direct `ItemUpdateData` handler stages.

The item adapter mirrors `ItemUpdateData.Serialize`:

- `Destroy` contains only update type and network ID;
- `PrefabName` appears only for `Create`;
- dropped/thrown state updates contain position and rotation, with throw direction only for thrown state;
- hand/inventory state updates contain player ID and no transform fields;
- attached state updates contain car ID and front/rear attachment only;
- tracked states appear only for `Create` or `ObjectState`;
- unused default runtime properties are omitted;
- locally useful values that were not transmitted are placed under a separate `context` object.

The same detached wire projection is captured immediately at serialization, decoded, handler-before, and handler-after stages. Item replication fingerprints are calculated from the wire projection rather than unused runtime fields.

Unity mathematical values are always projected directly as components. Vectors never expose `normalized`, `magnitude`, or `sqrMagnitude`, and quaternions never expose calculated Euler-angle or normalization properties. Unknown packet types use a shallow, cycle-aware, collection-limited public-field fallback that omits arbitrary properties and Unity object internals and reports truncation explicitly.

### Trace modes

- `Summary`: packet identity, routing information, size, and fingerprint.
- `Decoded`: summary plus detached decoded fields at targeted handlers.
- `Raw`: decoded visibility plus raw Base64 where allowed.

Raw mode is off by default. Login payloads are never stored raw. Decoded fields whose names contain `password`, `token`, or `secret` are redacted. Other gameplay and chat payloads are retained raw only when Raw mode is explicitly enabled. Existing IP logging preferences remain authoritative.

### Targeted instrumentation

Detailed tracing currently covers the paths needed for item, player, train, and origin investigations.

Items include:

- registration and destruction;
- relevance entry and exit;
- snapshot dispatch, receipt, creation, and application;
- cache entry and cache reuse;
- local instantiation and creation;
- missing local representations;
- client send requests, server relay requests, and bulk sends;
- before/after Unity state application;
- validation acceptance and stable rejection reason codes.

Implemented item rejection reasons include `client-create-or-destroy-not-allowed`, `held-by-other-player`, `owned-by-other-player`, `unknown-sender`, and `unknown-network-entity`.

Non-host ID-zero items are classified before any interaction gate is changed. Player-owned items and objects present in `Inventory`, `ItemContainerRegistry`, `StorageInventory`, `StorageWorld`, lost-and-found, or storage item containers remain interactable and can use first-interaction adoption. This includes a player item dropped into the world: `StorageWorld` membership distinguishes it from scene-authored clutter. An active non-essential object absent from every player/storage registry is treated as native scene clutter, interaction-gated, and cached for a matching host `Create`. If storage services are not available yet, classification fails open and the bounded reconciliation queue checks again later rather than briefly cancelling a legitimate grab. Events include `item.unbound-classified`, `item.unbound-scene-item-cached`, and `item.late-scene-item-reconciled`.

ID-zero client items now use a minimal first-interaction adoption handshake rather than inventory restoration or save-file registration. There is no inventory preload sweep, save-entry matching, inventory eligibility validation, restore barrier, registration-specific interaction gate, or one-shot registration snapshot path. When the ordinary grab, release, throw, remove, equip, unequip, or inventory-drop path observes an interacted ID-zero item, the client sends its prefab, live state, transform, tracked state, and a short correlation token to the host. The host resolves the prefab, creates the canonical item, allocates a nonzero ID, applies the supplied state, and returns the mapping. The client binds the same Unity object and immediately runs the same general-purpose state observation path used by every subsequent transition. Generic client `Create` remains forbidden, and normal gameplay snapshots with ID zero remain blocked. Adoption events are `item.adoption-requested`, `item.adoption-accepted`, `item.adoption-rejected`, and `item.adoption-complete`.

Local item transitions are tracked against both the previously observed Unity state and the last state placed on the network. State precedence is `Thrown`, `InHand`, `InInventory`, `Attached`, `Removed`, then `Dropped`; a physics-held item therefore remains `InHand` even when its transform is still under `WorldMover.OriginShiftParent`. Grab/release/throw callbacks and postfix observation of `Inventory.EquipItem`, `Inventory.UnequipItem`, and `Inventory.DropItemFromHandsOrInventory` enqueue work on `NetworkedItemManager`, so an item that becomes inactive in inventory is still processed. `item.local-state-observed`, `item.local-state-unchanged`, `item.local-state-changed`, and `item.snapshot-suppressed` include the current state, previous observed state, previous sent state, previous sent placement player, network ID, inventory/container slot, equipped slot, active state, trigger, and suppression reason. Dirty callbacks request re-evaluation but do not themselves force an `ItemState` packet: repeated equip/grab callbacks with the same last-sent state and placement player are collapsed locally, while a same-state P1-to-P2 handoff, one-shot `Thrown`, and dirty tracked values still serialize. Adoption completion uses the same `GetSnapshot()` path and does not bypass or disable later transitions.

`Thrown` is treated as a one-shot network transition rather than a stable Unity state. After a local throw snapshot is emitted, or a received throw is applied, the observation baseline becomes `Dropped` without sending a second packet; this prevents the following frame from emitting a synthetic dropped update that zeroes the remote rigidbody's momentum. Every item envelope also carries the authenticated originating player ID. The originating client consumes its echoed accepted throw as an authority/revision acknowledgement without running the Unity throw transition again, preserving the already-running local velocity and angular velocity; the host and other clients still apply the physical throw normally. `item.local-throw-acknowledged` records the preserved velocities. Listen-host local transitions are likewise metadata-only after `GetSnapshot()`: the base-game Inventory/GrabHandler has already applied the Unity transition, so the canonical authority pass must not run owner-claim inventory repair or a second drop. The `item.ownership-transition.before/after` payload identifies this with `unityStateAlreadyApplied=true`. Broad inventory-status notifications queue only items whose computed state actually changed, rather than re-observing every carried item. The combined dashboard likewise creates replication-flow operations only from send, receive, validation, apply, relay, delivery, and missing-representation stages; local unchanged and suppressed observations remain searchable events but never become permanently pending operations.

Host-local item changes never travel through the client-to-server validation handler. They remain dirty until the authoritative server tick creates or updates the item and distributes it through normal interest replication, preventing host `Create` snapshots from being rejected as client creates. On receiving clients, actual remote-hand membership is recognized as `InHand`, and observations for an item owned by another player emit `item.snapshot-suppressed` with `remote-player-authoritative` instead of echoing a false local `Dropped` update back to the host. A server without a local remote-hand representation preserves its last accepted held/inventory state until the owning player sends a transition.

Item ownership is now observed from the host's `AuthoritativeItemRegistry`. Its record deliberately separates persistent owner, current physical placement/possessor, and retained inventory claim. Every item snapshot and schema-aware packet projection includes `authorityRevision`, `persistentOwnerPlayerId`, inventory claim player/slot/flags, and transition reason. `item.canonical-record-created`, `item.transition-requested`, `item.transition-accepted`, `item.transition-rejected`, `item.ownership-transition.before`, `item.ownership-transition.after`, and `item.holder-invariant-violation` expose the decision and its Unity projection. Stale revisions and non-possessor transitions receive stable rejection reasons instead of silently changing parallel ownership fields.

Essential-item Get is a dedicated host-authoritative recall operation rather than the base UI's local `AddItemToInventory` call. Normal recall is transactional: the host computes an uncommitted candidate, the owner's runtime prepares the Unity inventory insertion, and the host commits only after successful confirmation at the same canonical revision. Failure, revision drift, disconnect, or the five-second timeout retains the prior canonical placement and sends a correction projection. `item.recall-requested`, `item.recall-prepared`, `item.recall-prepare-requested`, `item.recall-prepare-succeeded`, `item.recall-prepare-failed`, `item.recall-transaction-committed`, `item.recall-transaction-cancelled`, `item.recall-transaction-timeout`, `item.recall-accepted`, `item.recall-confirmed`, `item.recall-rejected`, `item.recall-applied`, and `item.local-possession-revoked` show the complete transfer. Each peer purges stale local inventory/container membership before applying a different player's possession. Foreign-owned inventory icons are red with a red outline and cannot invoke local Get. No duplicate object is created by recall.

An item whose canonical placement is Lost and Found is excluded from ordinary item interest and dirty-state processing even though its inert host Unity object remains registered. A stale client cannot resurrect it through pickup, inventory, hand, drop, or tracked-state packets: `item.lost-and-found-live-transition-rejected` records the attempt and the host returns a Lost and Found `Destroy` projection to quarantine that client's stale representation. The host reasserts one tombstone per ready client after collection (`item.lost-and-found-tombstone-reasserted`), and each client retains that tombstone locally: every representation with the NetId is made non-interactable and inactive again if base-game interaction callbacks reactivate it. Foreign inventory representations with that NetId are purged together so a thief cannot retain a red dropped silhouette or lose the slot; only the persistent owner's genuine retrievable claim may remain. Only an explicit retrieval `Create` clears the tombstone.

Derail Valley keeps a reserved/dropped slot for an essential item both while it is equipped and after it leaves the hand through the throw path, even if multiplayer says that another player persistently owns it. Inventory transition finalisation preserves this slot while the foreign item is actively equipped or grabbed, then removes the foreign local reservation once possession ends and emits `item.foreign-dropped-silhouette-purged`. Dragging the same item out already follows the base game's purge path; both drop interactions therefore converge on no silhouette for the non-owner while the owner's canonical stolen/retrieval claim remains intact.

Every `NetworkedItem` now has an exactly-once tracked-value finalization lifecycle owned by `NetworkedItemManager`. Specialized patches can explicitly finalize after registering their values; otherwise the manager's real-time grace deadline finalizes even zero-value items and drains deferred snapshots. Because the deadline is manager-owned, inactive inventory and cached objects still finalize. `item.tracked-values-finalized` records the reason, tracked-value count, and drained snapshot count, while `item.tracked-value-registered-late` identifies registration that missed the grace period.

Inventory, storage, and container diagnostics subscribe to Derail Valley's existing transition events rather than scanning all items. `inventory.transition-observed` retains both slot snapshots and both raw `InventoryActionType` flag values, while `inventory.item-transition` adds the affected item's live inventory/equipped slot, container identity, storage memberships, active state, layer, parent, and transform. `storage.item-added`, `storage.item-removed`, `container.registered`, `container.unregistered`, `container.contents-changed`, `container.item-dropped`, and `container.nesting-changed` expose the corresponding base-game boundaries. Dropped/reserved inventory claims are reported separately from physical storage placement.

The overlay's `6 INVENTORY` page lists the local inventory by slot, including dropped essential-item silhouettes. Each row distinguishes stored, silhouette, stolen, and foreign states and opens the normal item inspector with live inventory slot/equip slot, active versus including-dropped membership, reserved/dropped/locked flags, essential/player-owned classification, canonical claim slot, claim-slot agreement, persistent owner, authority revision, and replication timeline. ID-zero inventory objects remain visible using their Unity instance identity.

The `7 LOST+FOUND` page separates the four Lost and Found representations that otherwise look deceptively similar in ordinary item state: host registry records already collected, host policy candidates, save records awaiting owner/object rebinding, and the local client's authoritative list received from the host. Rows show source, status, compact Lost and Found handle, current runtime NetId, revision, owner, and collection or policy reason. Selecting a host record also exposes its persistent item UUID; that UUID is save/host-only and is never included in normal Lost and Found packets. The remaining details include claim slot/flags, authority placement, collection time, eligibility/grace timing, owner and nearest-player distances, grabbed/snapped/machine protection, canonical Unity-object presence, storage membership, active state, transform, and the underlying item's retained timeline. Search also matches these state values. The page gathers detailed Unity/storage state only while visible; the normal host policy scan caches cheap detached decision primitives to avoid introducing a periodic debug hitch. Summary/full copy modes and the standard event-copy controls work on the page.

Essential inventory claims are treated as immovable owner reservations. A normal owner drop, throw, or equip preserves the original reserved/dropped slot even while the physical item is in world storage or another player's hand. A foreign pickup adds the canonical `Stolen` claim flag without moving the owner's claim. Recall clears both `Dropped` and `Stolen`, then revives the existing reserved entry in place rather than inserting a second inventory object. On the borrower's process, authoritative possession revocation purges both the physical inventory entry and any dropped silhouette so no unusable ghost remains. `inventory.essential-claim-repaired`, `inventory.essential-claim-restored`, `inventory.essential-claim-invariant-violation`, and `item.essential-claim-slot-move-rejected` expose recovery or disagreement.

`item.respawn-or-destroy-scheduled` traces `RespawnOnDrop` before its delayed coroutine can independently reset, destroy, deactivate, or move an item to lost and found. The warning includes identity, ownership, essential status, delay, respawn mode, transform, velocity, hierarchy state, and whether the object is on a valid respawn parent. Subsequent storage events make the resulting move correlatable without changing the base-game lifecycle decision.

Generic interest synchronization determines job-document exclusions from `NetworkedItem.TrackedItemType`, not from the wrapper component type. Job-created overview, booklet, and report objects claim the authoritative item ID directly; any earlier generic representation is deactivated and moved to the cache before lookup ownership changes. Relevant events are `item.generic-create-suppressed`, `item.generic-create-bound-existing`, and `item.authoritative-binding-collision`.

Applying `Dropped` or `Thrown` now emits `item.detach.before`, `item.detach.after`, `item.hand-membership.before`, `item.hand-membership.after`, and any `item.parent-changed` transitions. After detachment it normalizes inventory-created canonical objects into world presentation: clears player ownership, activates the hierarchy, assigns the `World_Item` layer, enables renderers, restores physics, parents directly to `WorldMover.OriginShiftParent`, then applies the authoritative transform and throw. It deliberately does not route the object through the host player's drop position. The detailed before/after payload records the stored and computed states, grabbed state, actual transform parent and path, active state, layer, renderer counts, remote-hand membership, inventory/container/storage membership, rigidbody state, `GrabHandlerItem` state, and `ItemReparentingBase.CurrentParent`. `item.post-apply.frame+1` and `item.post-apply.frame+5` detect systems that reattach or otherwise overwrite the result after the handler returns. `item.world-state-invariant-violation` warns if the object becomes inactive, kinematic, inventory-layered or contained, loses all enabled renderers, or leaves the origin-shift parent at apply, frame +1, or frame +5.

Players include:

- join and disconnect;
- tracking handler/application stages;
- sampled tracking-applied events;
- car attachment changes;
- VR and posture-related state available from the networked player.

Trains include:

- train-car registration and removal;
- sampled physics receipt and before/after application;
- control/port registration and sampled value changes;
- control-authority application and validation.

Implemented authority rejection reasons include `sender-out-of-range`, `authority-held-by-other-player`, and `authority-release-not-owner`.

World lifecycle includes:

- `WorldMover.AboutToMoveWorld` as `world.origin-shift.before`;
- `WorldMover.WorldMoved` as `world.origin-shift.after`;
- previous/current move, shift vector, shift count, origin parent, player-local position, canonical absolute position, and current car;
- scene load and unload events.

## Entity registry

The debug-only registry supplements the mod’s existing network-ID dictionaries. It never replaces them.

Supported record types are:

- `Item`
- `Player`
- `TrainCar`
- `Trainset`
- `Job`
- `Station`
- `Control/Port`

Each record contains a weak Unity reference, display name, latest detached state, severity, last update time, and a bounded event timeline.

The low-frequency reconciliation pass is phased so it does not scan every entity together. Item reconciliation processes at most 32 items during its phase; player, train, trainset, job, and station work occurs in separate phases.

### Live state refresh

Lifecycle and packet events update registry state, but moving Unity objects can change between network applications. The selected inspector entity and visible verbose labels therefore request a live snapshot from their weak Unity reference.

Live refresh is:

- performed only on Unity’s main thread;
- throttled per entity to no more than once every 200 ms;
- shared between the inspector and labels so the same entity is not sampled twice;
- limited to the selected object and at most 16 verbose label candidates;
- separate from the broad reconciliation scan.

This keeps moving values current without restoring the earlier high-cost behavior of repeatedly snapshotting hundreds of entities.

Current live item state includes:

- prefab and item state;
- Unity instance ID;
- active-self and active-in-hierarchy state;
- parent and GameObject path;
- holder/player ownership;
- local and absolute position;
- rotation, velocity, and angular velocity;
- inventory, world-storage, and lost-and-found membership.

Player state includes identity, VR mode, on-car state, occupied car, transform, absolute position, rotation, scene, path, and activity. Train-car state includes car ID, derailment, velocity, last processed physics tick, transform, absolute position, scene, path, and activity.

The local player is registered using the multiplayer session’s assigned player ID and appears as `<name> (local)` in the Players tab. It includes the local camera, car, origin, transform, and VR state. Its world label is intentionally suppressed so it cannot appear directly in front of the local desktop camera or VR headset.

## In-game overlay

The overlay is a persistent uGUI canvas styled after the useful UnityExplorer layout conventions. It uses a dark header, green highlights, filter tabs, a pooled entity list, status cards, a structured inspector, and a separate verbose timeline. Its Packet tab provides a bounded, filtered view of retained packet events; it does not attempt to render every packet continuously.

The default compact layout occupies approximately 73.5% of the screen width and 69% of its height, leaving the right and lower game view visible. Expanded mode uses most of the screen. The header can be dragged with a mouse when one is available.

Panels and information include:

- session role, PID, and current network tick;
- entity count and active filter;
- world origin and local player absolute position;
- trace mode, sampling ratio, and firehose port;
- selected entity identity and live/frozen status;
- selected state age in milliseconds;
- structured latest state;
- the last 60 selected-entity events in verbose mode.

Fields that change in the selected live snapshot are highlighted green with `*` for 1.5 seconds. This makes holder, parent, state, position, velocity, car attachment, and other application side effects visible without comparing snapshots manually.

The overlay currently lists and selects `Item`, `Player`, and `TrainCar` records. Other registry types remain available through the HTTP entity API. The search box matches entity type, network ID, and display name before the 500-item display cap is applied, so an item omitted from the normal list can still be found directly.

Mouse-wheel sensitivity is increased for the entity list, state inspector, and timeline. Click-and-drag scrolling remains available.

### Keyboard controls

| Key | Action |
| --- | --- |
| `F6` | Open or close display/label settings |
| `F7` | Switch compact/expanded size |
| `F8` | Open or close the overlay |
| `F9` | Enable or disable world labels |
| `F10` | Freeze or unfreeze the selected inspector snapshot |
| `F11` | Enable or disable verbose inspector and label content |
| `F12` | Select the item, player, or train under the centre of view |
| `0` | Show all supported overlay entity types |
| `1` | Filter to items |
| `2` | Filter to players |
| `3` | Filter to train cars |
| `4` | Open the packet inspector |
| `5` | Open multiplayer health and automation diagnostics |
| `6` | Open the local inventory diagnostics page |
| `7` | Open Lost and Found registry, policy, save-rebind, and client-list diagnostics |
| Up/Down, Page Up/Page Down, `[`/`]` | Move through the filtered entity list |
| `Ctrl+F` | Focus the entity/packet search box |
| `Ctrl+C` | Copy the selected entity DTO as formatted JSON |
| `Ctrl+Shift+C` | Copy the selected entity’s complete retained timeline |
| `Ctrl+Alt+C` | Copy the selected entity’s latest event |
| `Ctrl+B` | Copy a diagnostic bundle containing session, trace settings, world/origin state, and the selected entity |

Opening the overlay temporarily unlocks and displays the cursor. Closing it restores the previous cursor visibility and lock mode. All primary selection and filtering operations remain usable without a mouse.

Freezing only holds the inspector’s detached snapshot. It does not pause networking, physics, registry updates, events, captures, or world labels. The selected header displays `FROZEN` or `LIVE` to make this explicit.

The selected header also has a `COPY: SUMMARY` / `COPY: FULL` toggle plus `COPY VIEW`, `COPY EVENTS`, `COPY LAST`, `COPY FLOW`, `COPY MATRIX`, and `COPY BUNDLE` buttons. Summary mode is the default and copies the same compact, plain-text representation shown by the overlay rather than serializing the complete DTO. Full mode preserves the structured JSON diagnostic export. The mode applies consistently to entity, packet, timeline, last-event, replication-flow, host-matrix, and bundle copies. Flow and matrix export only the selected item's replication-flow or host-interest data. The same operations are available as `debug copy flow` and `debug copy matrix`; `debug copy mode` toggles summary/full. Copy feedback includes the active mode and character count.

### In-game packet inspector

The Packet tab reads the already-bounded structured event store. Opening it does not add another network connection, replace packet handlers, decode the payload a second time, or change multiplayer behavior. Entity DTO lists are not rebuilt while this tab is active, which avoids combining registry-refresh work with packet-list refresh work.

The visible list is capped at the newest 1,000 matching packet events. Packet rows use a dedicated 54-pixel two-line height and the packet pane expands to 440 pixels while active. Normal mode lists only actual raw send/receive boundaries; verbose mode or an active search additionally exposes serialization, decoded, handler-before, and handler-after stages. This prevents the normal list from showing four or five overlapping rows for one logical packet.

Selecting a row displays an inspection bundle containing the selected structured event and nearby related stages with the same packet type or payload fingerprint. It includes direction, peer, channel, delivery method, byte length, fingerprint, decoded fields, and Raw Base64 where available. The on-screen JSON is capped at 64 KiB to protect the UI from exceptionally large payloads; switch to `COPY: FULL` and use `COPY VIEW` for the complete inspection bundle. The lower timeline shows the last 60 retained events for the selected packet type. `COPY EVENTS` copies those compact lines in Summary mode or up to 500 retained structured events in Full mode.

Packet filtering and controls are intentionally local to the inspector:

- the search box matches packet type, stage/event name, direction, runtime side, peer, channel, delivery method, entity, and fingerprint;
- manifest entries marked `SuppressByDefault` are hidden initially;
- `NOISY` shows or hides those high-volume packet types;
- entering a packet search also includes normally suppressed types, allowing an exact noisy type to be found without enabling all noise;
- `MUTE` removes the selected type from this UI only and does not stop capture, logging, or networking;
- `TRACE` bypasses high-frequency sampling for that selected packet type until it is untraced;
- `PAUSE` freezes only the displayed packet list; packet receipt, processing, logging, and the bounded event store continue normally;
- `SUMMARY`, `DECODED`, and `RAW` change the live trace mode for events captured after the button is pressed;
- `DECODED` creates schema-aware detached wire projections for every newly serialized outgoing packet and targeted incoming handlers; important item packets use explicit adapters and unknown packets use the strict field-only fallback;
- `RAW` also retains Base64 payload bytes at eligible raw and serialization/send stages and should be enabled only for focused tests;
- old events are labelled `PREVIOUS MODE` because mode changes cannot recreate fields that were not retained when those events occurred;
- sensitive login payloads remain redacted in every mode.

In the Packet tab, `P` toggles display pause, `N` toggles noisy types, `M` mutes/unmutes the selected type, and `T` traces/untraces it. The normal arrow, Page Up/Page Down, and bracket navigation keys select packet rows without a mouse.

### Selected-item replication diagnostics

Every selected item inspector includes a replication-flow summary. It reports the latest retained occurrence of the important stages already emitted by the existing item, packet-handler, validation, apply, and relay instrumentation:

```text
item.snapshot-created
item.packet-send-requested
packet.handler.before
item.validation-accepted or item.validation-rejected
item.snapshot-received
item.snapshot-apply.before
item.snapshot-apply.after
item.relay-requested
item.missing-local-representation
```

This is an observational reconstruction from the selected entity's bounded timeline. It does not add an identifier to gameplay packets or change their serialization. A missing stage means that stage was not present in the retained local process timeline; cross-process confirmation still comes from the combined dashboard or aligned captures.

On a host, the selected item also displays one row per server player from the real `KnownItems` and `NearbyItems` maps. Each row includes loading state, distance, nearby/known membership, the player's known tick, the item's last dirty tick, and the current inferred replication decision:

```text
not-ready
outside-interest
create-required
full-sync-required
up-to-date
```

The matrix is unavailable on a client because the authoritative per-player interest collections exist only in the server process. `COPY FLOW` and `COPY MATRIX` export these sections independently as formatted JSON.

### Health tab

Press `5` or select `5 HEALTH` to open the consolidated health view. This tab deliberately avoids rebuilding the entity list while active.

It contains:

- latest, average, jitter, and maximum transport latency;
- average network-tick interval and tick scheduling jitter;
- inbound/outbound packet and byte rates measured at the existing raw receive/send boundaries;
- structured events per second;
- average/maximum measured observability-operation duration;
- average/maximum complete debug-runtime update duration;
- queue depth, maximum depth, oldest age, received/applied counts, stale discards, tick gaps, and largest gap;
- unresolved item dependencies and their age;
- current network-ID integrity findings;
- automatic-capture status, count, and last trigger.

Train rigidbody, bogie, and speed queues report through the existing `TickedQueue<T>` path. Instrumentation increments bounded counters when a snapshot is received, rejected as stale, applied, or cleared. It does not change queue order, tick comparisons, or processing.

Item snapshots waiting for tracked-value finalisation appear as `ItemPendingSnapshot` queues. The dependency list currently reports:

- `tracked-values-not-finalised`;
- `missing-player`;
- `missing-train-car`;
- `missing-snap-point`;
- `attachment-failed`.

Resolved dependencies are removed from the active list. This is a health registry, not a retry mechanism, so it does not alter item behavior.

The item ID auditor runs every five seconds. It detects:

- more than one live item using the same nonzero network ID;
- active live items using ID zero;
- a live object's ID resolving to a different lookup object;
- a single Unity instance retained under multiple IDs in the internal item lookup.

The last check uses low-frequency debug-only reflection because the underlying lookup dictionary is private. It observes the existing dictionary and does not modify it.

### Automatic diagnostic captures

Automatic captures are opt-in because capture files retain every structured event during their active window. Toggle them with `A` while the Health tab is open or with:

```text
debug auto on
debug auto off
```

Triggers currently include:

- validation rejection;
- desync detection;
- error-severity structured events;
- a queue remaining nonempty for more than two seconds;
- an unresolved dependency older than two seconds;
- error-severity network-ID integrity findings.

Each automatic capture includes the normal two-second pre-roll and stops after eight seconds. Safeguards are a 60-second global cooldown, a five-minute cooldown for the same condition, and a maximum of ten automatic captures per process session. Manual captures are never interrupted to start an automatic capture.

### Display settings panel

`F6` or `SETTINGS F6` opens a keyboard- and mouse-operable popup without enlarging the main inspector. It controls:

- world-label scale;
- label radius;
- maximum label count;
- label update frequency;
- overlay UI scale;
- labels enabled;
- through-wall rendering;
- errors-only filtering;
- item, player, and train label types.

Within the panel, `-`/`+` changes label scale, comma/period changes radius, `Ctrl+S` saves, and Escape closes the panel. The Save button writes the values through the normal mod settings path.

### Gaze selection

Gaze selection uses the active desktop camera or VR headset camera. It first performs a narrow sphere cast through the centre of view and accepts child colliders belonging to registered items, remote players, or train cars. The local player ID and local player transform are explicitly excluded so the camera/player rig cannot win every selection. If nothing is hit, it selects the registered non-local entity within an eight-degree view cone that is closest to the gaze direction, up to 150 metres.

The same operation is available with:

```text
debug select look
```

## World-space labels

World labels are pooled runtime TextMeshPro objects that face `PlayerManager.ActiveCamera`. They support items, players, and train cars.

Behavior:

- update at a configurable 1–10 Hz, defaulting to 4 Hz;
- normal mode displays at most 64 labels;
- verbose mode displays at most 16 labels because each label includes live state;
- selected entities are prioritized first, followed by higher severity and then distance;
- default radius is 30 metres;
- labels behind geometry are hidden unless “through walls” is enabled;
- scale is clamped by distance for desktop and VR readability;
- labels are disabled by default.

Normal labels show entity ID, display name, type, and warning/error severity where applicable.

Verbose item labels show item state, holder, local and absolute coordinates, velocity, parent, storage membership, activity, and last event/tick. Verbose player labels show car attachment, VR state, coordinates, and last event. Verbose train labels show car ID, derailment, physics tick, coordinates, velocity, and last event.

The transform of a label follows the live Unity component during each label update. Its displayed state uses the bounded live refresh described above, which remains capped at 5 Hz per entity even if label positioning is configured above 5 Hz.

## Local debug command

The Derail Valley terminal command is process-local and is never sent through multiplayer chat.

```text
debug status
debug sessions
debug labels on|off
debug labels items|players|trains on|off
debug labels radius <metres>
debug labels errors-only
debug labels netid <id>
debug select look
debug select item|player|car <id>
debug dump
debug copy entity|timeline|last|flow|matrix|bundle|mode
debug settings
debug verbose
debug mark <text>
debug capture start <name>
debug capture stop
debug auto on|off
debug raw on|off
debug trace item <id>
debug trace packet <packet-type>
debug clear
```

Notes:

- `debug dump` prints the selected detached entity DTO as formatted JSON.
- `debug copy` writes the requested JSON representation to the system clipboard.
- `debug trace item` bypasses normal high-frequency sampling for that item.
- `debug trace packet` bypasses sampling for the named high-frequency packet type.
- `debug clear` clears the bounded in-memory event store; it does not erase existing log or capture files.
- `debug labels errors-only` currently enables that filter; there is not yet a matching command to turn only that filter back off.

## HTTP/SSE firehose

Each process chooses an available random port by binding to `IPAddress.Loopback` with port zero. It listens only on `127.0.0.1`. Failure to start the firehose does not stop file logging or the rest of the debug runtime.

The embedded server does not enable CORS. Mutating endpoints require the per-session token from the local discovery file in the `X-DVMP-Debug-Token` header.

### Read endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /` | Embedded Web UI |
| `GET /app.css` | Embedded UI stylesheet |
| `GET /app.js` | Embedded UI logic |
| `GET /events` | SSE stream, beginning with the current bounded in-memory snapshot |
| `GET /api/session` | Current process session metadata |
| `GET /api/sessions` | Sessions represented by this server; useful in dashboard mode |
| `GET /api/event/{sequence}` | Full event detail retained in this server’s store |
| `GET /api/entities` | Entity snapshots and timelines |
| `GET /api/entities/{type}/{id}` | One entity record |
| `GET /api/settings` | Runtime trace settings |
| `GET /api/replication` | Bounded item-replication operation summaries in dashboard mode |
| `GET /api/replication/{operationId}` | Complete correlated operation, recipient expectations, stages, and resulting states |

### Token-protected endpoints

| Endpoint | Purpose |
| --- | --- |
| `POST /api/settings` | Change trace mode, raw capture, sampling, and enabled categories |
| `POST /api/mark` | Add a timestamped marker event |
| `POST /api/capture/start` | Start a named capture with optional shared capture ID |
| `POST /api/capture/stop` | Stop the active capture |

Each SSE subscriber has a bounded queue of 2,048 events so a slow browser cannot block Unity or unboundedly grow memory.

## Web UI and combined dashboard

Open the `firehoseUrl` in a process discovery file to inspect one game process directly.

The Web UI supports filters for:

- session;
- category;
- event text;
- severity;
- entity type and ID;
- packet type;
- direction;
- application stage.

It also exposes marker creation, capture start/stop, Raw mode, entity/session inspection, and full event details.

To combine all live host and client sessions without joining the multiplayer game as a peer:

```powershell
Multiplayer.DebugClient.exe --dashboard "<path-to-Multiplayer.Debug>"
```

If no path is supplied, dashboard mode first looks in the standard Derail Valley mod installation and otherwise uses `Multiplayer.Debug` below the current directory.

Dashboard mode:

- scans discovery files every two seconds;
- rejects stale or mismatched processes;
- connects to every live session’s SSE stream;
- holds events for a 250 ms reorder window;
- orders by UTC timestamp, session ID, and source sequence;
- preserves a stable `sessionId:sourceSequence` event key even after assigning dashboard-local sequence numbers;
- broadcasts settings, markers, and capture commands to every live process using each process token;
- assigns one shared capture ID to coordinated captures.

The `Item replication` page turns item events from all discovered host/client processes into bounded replication operations. It shows:

- item ID and update type;
- origin process/player and correlation confidence;
- per-process flow lanes with relative milliseconds and network ticks;
- the host's per-recipient delivery expectation and interest/known-item decision;
- sent, received, handled, and applied status for each client;
- resulting origin, host, and client state fields side by side;
- explicit `sent-not-received`, `received-not-applied`, and validation-rejection discontinuities;
- compact and full-operation clipboard exports;
- filters for item, update type, status, and problems only.

Item snapshots receive a canonical state fingerprint whose object/dictionary property ordering is normalized. Exact fingerprints correlate unchanged snapshots across processes. Item-ID and short-time-window matching is retained as a visible lower-confidence fallback for application stages that do not carry a snapshot.

The host emits `item.delivery-expected` immediately before each existing real recipient send. The event records recipient player/peer, loading state, delivery method, known/nearby state, known tick, dirty tick, and the current send decision. This instrumentation is guarded and does not select, add, remove, or reorder recipients.

`Auto captures` on the replication page is off by default. When enabled, the first newly detected replication discontinuity starts the same shared capture on every discovered process, records five seconds, then stops it. It is limited to ten captures per dashboard run with a fifteen-second coordinator cooldown.

On the same-machine test setup, an expected reliable item delivery becomes `sent-not-received` after 750 ms. A received update remains pending for two seconds before becoming `received-not-applied`, allowing initial-sync registration/dependency queues time to resolve. A late receive or apply clears the recipient discontinuity and allows the operation to recover to complete. The originating client intentionally acknowledges its echoed authoritative `Thrown` revision without applying the throw a second time; `item.local-throw-acknowledged` therefore completes that recipient as an acknowledgement while keeping `applied=false`.

The dashboard itself has its own local random-port Web UI and never connects as a multiplayer peer.

### Local grabber recovery

Rapid inventory changes and forced item detachment can leave Derail Valley's non-VR `Grabber`
state machine in `Holding` or `Dragging` after its held/dragged handler has already detached. In
that state world pickup hover disappears and hotbar force-hold requests are ignored. The
multiplayer watchdog waits eight consecutive frames before repairing only these impossible state
combinations. `interaction.grabber-stale-state-recovered` includes the old state, held handler,
activity and interaction flags, and the selected recovery action. Healthy holding, dragging and
short-lived transition frames are not changed.

Remote `InHand` and `InInventory` projections are evaluated against their authoritative
multiplayer placement. They are not passed through Derail Valley's local-only inventory lookup;
doing so previously reported every other player's inactive inventory representation as `Dropped`.

## Captures and markers

Markers produce `marker.created` events and are useful for aligning a physical test action with host/client logs:

```text
debug mark client throws flashlight inside moving train
```

Starting a capture creates a JSONL file containing:

1. capture metadata and the complete session snapshot;
2. a unique or dashboard-shared capture ID;
3. up to two seconds of pre-roll from the bounded event store;
4. every subsequently published event until capture stop.

Capture names are sanitized for filenames and limited to 80 characters. Starting a new capture stops the current capture first.

Raw bytes are present only in events produced while Raw mode was active. Turning Raw mode on later does not recreate bytes omitted from earlier summary events.

## Desync detection

The current detector is an initial item-focused pass. It remembers received authoritative item snapshots and samples at 2 Hz.

It detects:

- a missing local `NetworkedItem` representation;
- holder mismatch for in-hand and in-inventory states;
- dropped-item position differences greater than 0.5 metres.

A mismatch must occur in two consecutive samples before `desync.item-detected` is emitted. When the comparison becomes valid again, it emits `desync.item-recovered`.

Thrown items are intentionally not compared forever against their original throw position. Their immediate before/after application state and later sampled live trajectory are available for host/client comparison without treating a moving body as a static authoritative point.

Not yet implemented in the automatic detector:

- complete parent, rotation, activity, storage, and duplicate-representation checks;
- player coordinate-frame and car-attachment comparisons;
- train track-relative, rigidbody, derailment, and authority comparisons;
- automatic cross-process comparison inside the game processes.

The replication dashboard now compares correlated post-apply states across processes. More specialized semantic tolerances for rotation, storage, parentage, and moving thrown-item trajectories remain future work; their raw fields are already shown in the state table.

## Performance and failure isolation

The runtime is designed not to stall Unity’s main thread:

- every call site is guarded by the enabled category;
- high-frequency sampling occurs before expensive decoded snapshots;
- packet hashes and payload fingerprints are calculated without a second protocol decode;
- JSON serialization and normal file writes happen on a background worker;
- the JSONL queue is bounded and drops rather than blocking when saturated;
- the global and per-entity histories are bounded;
- overflow preferentially removes sampled/high-frequency or trace events before more important records where possible;
- SSE subscriber queues are bounded;
- entity reconciliation is phased;
- item reconciliation handles at most 32 items in its phase;
- world labels are pooled, distance-limited, and updated at 4 Hz;
- verbose labels are capped at 16;
- live state refresh is per-entity throttled and limited to visible/selected entities;
- raw payload retention is opt-in.

The firehose, JSONL sink, discovery writer, and capture sink handle I/O failures without changing multiplayer behavior. Observability exceptions do not alter handler validation or application results.

### Current performance limitations

- The normal JSONL sink serializes and writes on a background thread, but the active capture writer currently appends synchronously with auto-flush when an event is published. Captures should therefore remain bounded test windows, particularly in Raw mode.
- The settings DTO contains future `TracedPacketTypes` and `TracedEntities` fields, but the HTTP settings endpoint does not yet apply those arrays to the command-based trace registries. Use `debug trace item` and `debug trace packet` for targeted sampling overrides.
- The asynchronous JSONL sink counts queue drops internally, but that dropped-event count is not yet exposed in the overlay or session API.

## Recommended item-sync test workflow

1. Start a host and one or more clients with the debug system enabled.
2. Start dashboard mode and open its printed loopback URL.
3. Enable labels only when spatial context is useful.
4. Use `F12` or `debug select look` to select the test item.
5. Use `F11` for detailed item label and timeline information.
6. Start a coordinated capture.
7. Add a marker immediately before the physical action.
8. Perform the grab, drop, throw, storage, job-item, or moving-train test.
9. Stop the capture and compare the same item ID across sessions.

For a client throw, look for this sequence:

```text
client item.snapshot-created
client item.packet-send-requested
client packet.serialized / packet.raw.send
host packet.raw.receive
host packet.handler.before
host item validation
host item.snapshot-apply.before / after
host item.relay-requested
remote packet.raw.receive
remote item.snapshot-received
remote item.snapshot-apply.before / after
live transform, velocity, parent, holder, and storage state
```

Use the world-origin cards and `world.origin-shift.*` events when positions differ by kilometres or a moving-train throw appears to use the wrong coordinate frame.

## Build and verification

Build the shared contracts, standalone client/dashboard, and mod with:

```powershell
dotnet build Multiplayer.DebugProtocol/Multiplayer.DebugProtocol.csproj -c Debug
dotnet build DebugRemoteClient/Multiplayer.DebugClient.csproj -c Debug
dotnet build Multiplayer/Multiplayer.csproj -c Debug
```

Run the shared runtime smoke tests with:

```powershell
DebugRemoteClient/bin/Debug/net48/Multiplayer.DebugClient.exe --self-test
```

The self-test currently covers event serialization/schema, redaction, bounded retention, stable payload fingerprints, discovery liveness, random loopback startup, API responses, and capture pre-roll/live writes.

Pure multiplayer authority invariants are covered separately by the NUnit suite:

```powershell
dotnet test Multiplayer.Core.Tests\Multiplayer.Core.Tests.csproj
```

Actual item packet wire fixtures and schema-aware debug projections run in a second NUnit suite:

```powershell
dotnet build Multiplayer\Multiplayer.csproj -c Debug
dotnet test Multiplayer.Protocol.Tests\Multiplayer.Protocol.Tests.csproj -c Debug
```

`Multiplayer.Core` has no Unity or Derail Valley dependencies, and the production host registry delegates its ownership, placement, revision, claim, and recall decisions to that tested state machine. See `TESTING.md` for the test boundary and extraction roadmap.

The same core now owns item send-projection comparison (`state + holder + attached car/end`), bounded pending-snapshot ordering, and inventory-claim planning through `IInventoryView`. Deferred queues reject duplicate/stale revisions, cap at 256 entries, and allow an authoritative `FullSync` to supersede older queued deltas. `item.pending-snapshot-rejected` reports duplicate, stale, or capacity rejection; `item.snapshot-deferred` includes capacity, enqueue status, and the number of snapshots superseded by a full snapshot.

Recipient completion, adoption coordination, storage placement, and remote hand/inventory projection are also pure core decisions. The dashboard uses `RecipientCompletionEvaluator` for expected/received/applied completion and the 750 ms / 2 s discontinuity boundaries. Client adoption permits one pending token per Unity item and ignores unknown or duplicate completed results; the host scopes tokens by authenticated player and replays the original completed token-to-NetId outcome. `item.adoption-request-suppressed` and `item.adoption-result-ignored` expose coordinator rejections. Storage world projection removes conflicting inventory, lost-and-found, and item-container memberships according to one plan; `storage.multiple-membership-repaired` reports overlap repair and `storage.transition-rejected` reports an unavailable/invalid projection.

NetId allocation, relevance hysteresis, known-item delivery selection, and tracked-value
full-sync/delta composition are now pure tested decisions too. The runtime allocator reserves zero,
rejects collisions and duplicate releases, reuses released IDs once, and reports exhaustion instead
of wrapping. Item relevance uses the existing inclusive 100 metre boundary and strict three-second
removal delay; delivery distinguishes Create, current Dirty, missed-tick FullSync, and no-op. Tracked
state composition prevents clients from leaking server-authoritative dirty values and plans unknown
or authority-rejected incoming keys before the Unity setters run.

Host/client gameplay behavior, world-origin shifts, and VR label readability still require in-game acceptance testing with separate processes.
