# Gadgets

> Status: designed 2026-10-21; PRs 1 and 2 built. Nothing below is built except where
> [Implementation status](#implementation-status) says so. Vanilla behaviour is in
> [`../research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](../research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md);
> this doc refers to its sections rather than repeating them.

Gadgets are the installable customization items: switches, lights, displays, ATS, DPU, amp limiter,
anti-slip and the rest, mounted on a car with a drill or tape, wired together, soldered to the car's
electronics. They were left until the client/server split was done, because almost every gadget
action changes the world in one game only and has to become a request to the server.

**Vehicles only.** Vanilla also lets gadgets be installed on the world, the player house, the Lost and
Found shed and the paint station, but nothing in the game is made for them: almost every gadget needs
soldering into a vehicle's electrics, which only vehicles have, and those anchors are half-finished
(no saved holes except the world's, and the shed's anchor jumps between sheds). In multiplayer a
gadget installs on a car or not at all (decided 2026-10-21); see [Off-train installs](#off-train-installs).

## Today

Nothing about installed gadgets syncs. Each action changes only the game it happens in.

- **Installing** on a client sends nothing. The item is deactivated in the same frame its release is
  observed, the release grace waits for a `LateUpdate` that never runs, and the server keeps the item
  in the client's hand, where everyone else sees it and where the server saves it. A listen host's
  player installing makes the server commit the inactive item as `World`, and clients see a loose
  gadget fall from the host's hand.
- **Removing**, **drilling**, **taping**, **wiring**, **soldering**: local only. Car holes reach other
  players only in a later spawn or join (`TrainsetSpawnPart`).
- **Installed gadgets in the server's save** are registered as `World` items at the stale pose of the
  inactive item: clients near that pose get a loose copy, they can be taken to Lost and Found, and they
  are written to `WorldItemsV1` as well as vanilla's installed storage. `NetworkServer` reads the
  installed list at `ReadyForItems` into an unused local.
- The two existing hooks never fire. `GadgetRemoverPatch` and the `ItemState.Removed` apply look for
  a component on the item that is on the projection, a separate object. `GadgetSwitchPatch` puts the
  switch value on the item, which is inactive while installed, and its knob can't open an item
  interaction because the projection isn't under the item.

## Who owns what

The server owns every gadget fact. Players ask; nobody else changes anything.

| What | Owner | How it reaches players |
| --- | --- | --- |
| Which gadgets are installed where, their pose and UID | server | the [change log](#the-change-log) and the [anchor snapshot](#late-joiners) |
| The gadget item's placement (`Installed` on an anchor, or anywhere else) | server, through the item registry | item updates, as for any item |
| Mount point states, mounts, holes in anchors | server | the change log |
| Wiring | server | the change log |
| Soldering progress | server | the change log; the soldering player predicts |
| Settings (switch values, modes, regimes, channels, text) | server | [gadget state](#gadget-state); the player who turned a knob predicts |
| Running state of server-run gadgets (ATS timer, roadrunner count, alternating phase, anti-slip active) | server | gadget state, rate-limited |
| Anything a gadget writes to a train (ports, controls, MU radio) | server, by running the gadget | the existing port, control and fuse sync |
| Displays, lamps, lights, power, outputs of wired switches, the remote signal booster's range | derived in every game from synced inputs | nothing extra |
| Other players using tools on gadgets: drilling, taping, soldering, hammering, wiring, and the place, remove and fill sounds | server relays | [Tool use](#tool-use-other-players-see) |
| Motion and randomness in gadgets: beacon spin, light sweep, the control stand's rain fault, hanger swing | shared clock, shared seed or server | [Shared motion and randomness](#shared-motion-and-randomness) |
| The acting player's own UI: aim previews and highlights, wire lines shown while aiming the crimping tool, interaction text, the label popup, the HUD | the acting player | local only |

Everything a player can see or hear in the world is the same in every game. Only a player's own UI is
theirs alone.

Gadgets are server-authoritative like the rest of the train system: the server keeps track of them and
broadcasts; clients never decide. A display is **derived** in each game only if every input it reads
is already synced, so every game shows the same thing without sending it twice; otherwise the server
sends it. The speedometer gadget is the example: it reads the wheel-speed port, which each game feeds
from its own copy of the car's synced motion, as the cab speedometer does (`locallyFedPorts`,
[trains.md](trains.md), "Brake and locomotive simulation"). Sending that port as well gave it two
writers and a flickering needle. It inherits that port's gap: a client's wheel speed doesn't include
the server's wheelslip, which is train sync's to fix.

Every change works the same on a player host and a dedicated host: the host's player is a
`LocalPlayerSession` whose requests go through the server's request queue like anyone's
([host-local-session.md](host-local-session.md)).

## Identity

- **A gadget is its item.** The item's NetId names the gadget in every packet; its persistent item id
  names it in the save. The projection has no identity of its own.
- **Anchors are cars.** On the wire an anchor is the car's NetId; in the save it is vanilla's
  `placedOn`, the `CarGUID`.
- **UIDs are the server's.** Vanilla's customizer UID is what wires, mounts and hanger snaps refer to,
  and every process allocates it from its own counter, so two games disagree after the first install.
  The server lets vanilla allocate it; every install it sends carries the UID; a client sets it with
  `AssignUID` **before** `Place`, so `OnAfterLinked` sees a non-zero UID and never allocates. A
  [predicted](#optimistic) install takes a provisional **negative** UID the same way (negative ones
  never raise vanilla's counter and can't collide with the server's), replaced by the server's on
  confirmation. A client that finds itself allocating one (a `Link` with UID 0 outside the server)
  logs it and asks for a [resync](#desync-and-rollback). Wires and mounts on the wire are
  `(anchor, UID)` pairs; the change log and the snapshot only carry UIDs the server assigned, and a
  request never carries a provisional one (a request about a gadget whose install is unconfirmed waits
  for the confirmation, while its effect is already predicted).
- `Index` is never used.

## The installed gadget and its item

`Installed` already exists in the item model (`ItemPlacementKind.Installed`,
`AuthorityPlacement.Installed`), is already non-spatial, not persisted by the mod's own records and
not eligible for Lost and Found. Nothing assigns it. Gadgets make it real:

- **The record.** `Installed` gains the anchor: the car NetId and `CarGUID`, as `Attached` keeps them
  for EOT lanterns. Pose, UID and gadget data are the gadget's, not the item record's.
- **Transitions.** Only the gadget server moves an item into or out of `Installed`, with change
  reasons `gadget-installed`, `gadget-removed`, `gadget-car-deleted`, `gadget-glass-broken`,
  `gadget-dislodged`. Installing needs the item in the requester's hand. Removing puts it in the
  requester's hand (by hand or telegrab) or in the world at the projection's pose (hammer, forced).
- **Relevance.** An installed item is relevant to every player past `ReadyForCustomizers`, as every car
  is. Before the change log mentions an item a player doesn't know, the server sends that player its
  Create (placement `Installed`, inactive). Once removed, it is a world item and spatial interest takes
  over.
- **On clients** an `Installed` item stays inactive; the item code never activates, streams, drops or
  destroys it. Applying an item snapshot that moves an item out of `Installed` while its projection is
  still linked first runs the native `Remove` in the apply scope, so the order of the item update and
  the gadget removal doesn't matter.
- **Ownership is unchanged by installing**, and never lost. The item keeps its persistent id and its
  owner's identity (`PersistentOwnerIdentity`, a GUID, valid while the owner is offline) through
  installing, removing, being knocked off and being carried away by someone else; vanilla's
  `BelongsToPlayer` stays too. Anyone may remove anyone's gadget ([Decisions](#decisions-for-the-user)).
- **The owner is shown.** Aiming at an installed gadget shows its owner's name in the interaction
  text. The server sends the name with the item's `Installed` record, so it shows while the owner is
  offline.
- **A deleted car sends its gadgets to their owners' Lost and Found at once**, online or not
  (`gadget-car-deleted`), instead of leaving them as world items for the distance rules. Unowned ones
  stay in the world at the gadget's pose.
- **No tether.** Nothing pulls an absent owner's gadget back to its train. Tracking every owned item
  for life, listing it and retrieving it from anywhere is the owned-items design's job (see
  [Owned items](#owned-items)); gadget sync only has to keep the id and owner intact and offer it a
  `Retrieve` removal.
- **Never destroy an installed item.** `GadgetItem` has no `OnDestroy`, so destroying one leaves a
  linked projection. The item code's destroy path, on any game, refuses an `Installed` item and logs
  it; destroying an uninstalled gadget item also destroys `item.Gadget.gameObject`.

## Requests

A player's gadget action becomes a request at the point where vanilla would commit it, and on a
remote client it is also [optimistic](#optimistic): the change shows at once and the server confirms
or corrects it. The rest of the tool (previews, progress, sounds) is vanilla's.

| Action | Where it is intercepted (Client role, any local player) | Request |
| --- | --- | --- |
| Install | `GadgetItem.TryPlace` (public static; every input path ends there, the load path doesn't). A prefix on `TryPlaceOnHit` notes the `Mount` that was hit. | `Install`: item, anchor, local pose, mount host UID, on-glass flags of the gadget and each mount point (computed from the hit collider and the prefab's mount points at the target pose, as `GeneratePlacementData` would) |
| Label | the same, with `instantiateGadget` | `SpawnInstall`: the label maker item, the prefab, anchor, pose, text (sent after the popup closes with text; a cancel sends nothing) |
| Remove | `GadgetRemover`'s remove, `GadgetHandNonVR.TryGrab` and `GadgetHandVR.TryGrab`, `GadgetBase.OnTelegrabbableChanged` | `Remove`: item, method (`Remover`, `EmptyHand`, `Telegrab`). A fourth method, `Retrieve`, is reserved for the owned-items system: the server removes the gadget on the owner's behalf and hands the item to that system |
| Bolt, tape, untape | `DrillTool`, `DuctTape`, `GadgetHandBase` at their `SetMountPointState` call | `MountPoint`: item, point index, state, the tape item for taping |
| Fill a hole | `HoleFiller` at its `RemoveHole` call | `FillHole`: anchor, hole local position |
| Wire, unwire | `GadgetWiringTool` at its `Wire`/`Unwire` call | `Wire`: anchor, source UID, target UID, wire or unwire |
| Solder | `GadgetSolderingTool.ProcessSoldering` | `Solder`: item, units (accumulated, sent at 5 Hz and on release), the tool item. [Predicted](#soldering). |

The intercepting patches pass straight through inside the **gadget apply scope**
(`Shared/Gadgets/GadgetApplyScope`), which the server sets while it applies a request and a client sets
while it applies the change log or its own prediction. That is how the same vanilla methods serve the
player's intent and the server's apply.

### Optimistic

Every gadget interaction is optimistic on a remote client (decided 2026-10-21: at 250 ms everything
that waited for the server felt laggy, and making train controls optimistic fixed that). The patch
sends the request and applies the change in the player's own game at once, in the apply scope, as
vanilla would have; the server's answer confirms it or undoes it. On the host the request is handled
within a frame, so the host's player is never predicted: there is no separate game to predict in.

| Action | Applied at once | Confirmed by | Undone on rejection by |
| --- | --- | --- | --- |
| Install | `Place` with a provisional negative UID; the item leaves the hand | the `Installed` entry echoing the operation: the server's UID replaces the provisional one | `Remove` (no holes: nothing is bolted yet) and the item back in the hand |
| Remove | `Remove`; by hand or telegrab the item goes to the hand | the `Removed` entry | a [resync](#desync-and-rollback) of the car: removal erased the UID, soldering and wires, which only the server's snapshot has |
| Bolt, tape, untape | `SetMountPointState` | the `MountPoint` entry | setting the previous state |
| Fill a hole | `RemoveHole` | the `HoleRemoved` entry | adding the hole back |
| Wire, unwire | `Wire` / `Unwire` | the entry | the opposite call |
| Solder | `MakeSoldered` with the player's units | the `Soldered` entry's absolute units | the server's units ([below](#soldering)) |
| Knobs, switches, setters | the setter | the state broadcast echoing the operation | the server's value, through `SyncControls` |
| Buttons and joysticks (ATS acknowledge, distance tracker, snooze, control stand) | the vanilla method's visible effect | the state broadcast | the server's state |

- **Items wait.** Anything that creates or destroys an item (the last duct tape swapped for an empty
  one, the spent solder reel, a new label) waits for the server, because items only get NetIds from
  the server. The rest of the same action is still applied at once.
- **Order.** The player's predictions sit on top of the car's confirmed state. Other players' entries
  keep applying underneath in revision order; a prediction they make impossible (someone else removed
  the plate the player is mounting on) is rejected by the server and undone.
- **Gadget knobs use leases like cab levers.** Grabbing a gadget's knob, lever or joystick takes that
  control's lease through the same path as a cab control, so two players never fight over one knob,
  and the holder's moves stream like a cab lever's. The HUD's buttons, which have no grab, send their
  change as a request without a lease.
- **The gadget wins.** When a server-run gadget acts on a control a player holds (ATS braking,
  anti-slip easing the throttle), the server revokes that player's lease, as vanilla makes the player
  let go (decided 2026-10-21; see [Logic that writes the train](#logic-that-writes-the-train)).
- Rejections are rare, because the player's own game ran vanilla's checks before predicting; each one
  is logged with its reason.

Every request carries an operation id; the result is either the change-log entry that echoes it or a
rejection with a reason (shown as interaction text). A player can keep working while earlier requests
are in flight (install a plate, then bolt it, at once): requests go reliable and ordered, the server
handles them in order, and one that names a gadget by a provisional UID (a wire to a gadget just
placed) is held until that install is confirmed. When a request is rejected, the server also rejects
any later one that depended on it, and the client undoes its predictions newest first.

### Off-train installs

`Customization.Resolve` finds the world (terrain and anything without a rigidbody), the house (any
plain item static parent), the shed or the paint station when the hit isn't a car. The install patch
refuses those itself, without a request: nothing is placed, the item stays in hand, and the interaction
text says gadgets go on vehicles. The label maker is refused the same way off a car (its labels on item
containers are `LabelableItem`, not gadgets, and unaffected). The server rejects a non-car anchor too.

A save from before multiplayer may hold gadgets installed off-train. When the server loads it, it
removes each of them natively into the world at its pose, as an ordinary item with its owner; nothing
installed off a car exists in a multiplayer session. World holes keep their join-only sync
(`ClientboundCustomizationsPacket`); no new ones can be made.

### What the server checks

The server runs vanilla's own checks as well as its own (decided 2026-10-21). Vanilla's placement and
surface checks need the `Gadget_Mesh_Placing` colliders, copies of the cab's surfaces that every car
builds from its interior prefab at setup, so they exist on the server too; vanilla only switches them
on while the local player is within 20 m of the car
(research: [Placement colliders depend on the local player](../research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md#placement-colliders-depend-on-the-local-player)),
which on a dedicated host is never. For a request on a car, the server switches that car's holder on,
runs vanilla's check, and switches it back:

- install: `GadgetItem.CanPlace` at the requested pose (in the car's frame, so the car's position and
  speed don't matter) and the "not on a hole" check; the mount points' on-glass flags are computed by
  the server as `Drillable.GeneratePlacementData` does, and the gadget's own on-glass flag from the
  request is accepted only with a window collider at the pose;
- bolting and taping: `Drillable.CheckIfCanChangeToState`.

A request vanilla's check refuses is rejected with that reason, so a misfire (a surface that exists
only in a loaded cab interior, a floating-point difference at an edge) shows up as a named rejection,
first in the scenarios on a dedicated host. The other checks:

- the player is loaded past `ReadyForCustomizers`;
- the item exists and is in the requester's hand (install), or is installed (everything else); the tool
  item (drill, tape, hammer, wiring tool, soldering iron, label maker) is in the requester's hand;
- the anchor is a car that exists and isn't being deleted;
- reach: the target pose is within 5 m of the requester (vanilla's reach is 3 m), measured in the car's
  frame when the requester rides that car;
- the pose is inside the anchor's placement bounds: the union of the `Gadget_Mesh_Placing` meshes'
  shared-mesh bounds in the parenting frame, which is valid while the colliders are inactive, plus the
  car's own bounds;
- mounts: the mount host is a gadget on the same anchor with a free `Mount` and enough attached points;
- removal: vanilla's `GetValidRemovalMethodsMask` allows the method; `EmptyHand` needs no taped or
  bolted point;
- mount points: the index exists, the transition is one vanilla allows (`Mounted` not on glass);
- wiring: both UIDs on that anchor, ports compatible (`TryGetCompatiblePorts`), not already wired for a
  wire, wired for an unwire;
- soldering: the target is solderable and `ArePlacementRequirementsMet`; units are clamped to the
  tool's remaining units, to what the gadget still needs, and to what the elapsed time allows (twice
  vanilla's 32768 units per second);
- the feature flags are the requester's and are not checked (only the tutorial or the console deny
  them);
- permissions: none yet, anyone may do anything to any car's gadgets. This is where the planned
  access-control system will plug in (see [Decisions](#decisions-for-the-user)).

Then it applies the change natively in the apply scope: `GadgetItem.Place` (and `Mount.MountGadget`),
`GadgetBase.Remove`, `Drillable.SetMountPointState`, `Customization.RemoveHole`, `WireLinkPort.Wire`,
`MakeSoldered`. Place's on-glass flags are overwritten with the checked ones after
`GeneratePlacementData`.
`Place` drops and purges the item from the server game's local `Inventory`; that is the requester's
on a player host, and harmless otherwise. The item registry transition is committed explicitly next to
the native call, inside the item code's own apply scope, so its observers never see the deactivation
as a drop.

For hand and telegrab removal the server puts the item in the requester's hand, and the requester's
game, on the result, does what vanilla did next: `RequestForceHold` (non-VR) or
`ForceGrabInteractable` (VR).

## The change log

The server doesn't describe its own changes; it observes them. Server patches on the native
primitives record what happened, whatever caused it: a player's request, a car deletion, a broken
window, a collision, a mount's cascade, a label's replacement.

Why observe rather than announce each request (agreed with the user 2026-10-21): announcing requests
would need its own code for every change that has no request behind it (car deletion, glass, the
random knock-off, mount cascades, labels replacing themselves), and a missed or newly added one
desyncs silently. The primitives are what all of those call; they are few and the least likely to
change. A primitive renamed by a game update fails its Harmony patch at load; a change that bypasses
them shows in the gadget digest every scenario compares; the one subtle rule (only top-level changes
are recorded) lives in `Multiplayer.Core` with unit tests. Requests still exist: they carry the
player's intent and get an answer; the log only carries the result.

| Primitive | Entry |
| --- | --- |
| `Customization.Link` of a `GadgetBase` | `Installed`: item, anchor, pose, UID, gadget data |
| `Customization.Unlink` of a `GadgetBase` | `Removed`: item, forced or not |
| `Mount.MountGadget` / `UnmountGadget` | `Mounted` / `Unmounted`: host UID, gadget UID |
| `Drillable.SetMountPointState` | `MountPoint`: item, index, state |
| `Customization.AddHole` / `RemoveHole` / `ClearHoles` | `HoleAdded`, `HoleRemoved` (local position, direction) |
| `WireLinkPort.Wire` / `Unwire` | `Wired` / `Unwired`: source UID, target UID |
| `MakeSoldered`, `SetSolderingUnits` | `Soldered`: item, absolute units |

- **Only top-level changes are recorded.** Each observed primitive raises a nesting depth while it
  runs; changes at depth above zero are consequences of the one that contains them (an unlink's holes,
  unwiring, unmount and soldering reset) and every game reproduces them by running the same native
  call. Removing a gadget is one `Removed`, not a dozen entries.
- **Payloads are captured at flush.** An entry holds the object; its payload is read when the log is
  flushed at the end of the server tick. `Installed` therefore carries the on-glass flags and soldering
  that `Place` sets after `Link`, as vanilla `gadgetData` (`GadgetItem.OnItemSaveDataRequestedInternal`).
- **One ordered stream per anchor.** Each anchor has a revision; each entry increments it. Entries go
  to every player past `ReadyForCustomizers`, reliable and ordered, in one
  `ClientboundGadgetChangesPacket` per anchor per tick, after the item Creates they need. Entries that
  answer a request carry its operation id.
- **Before the server is running** (the vanilla save loading) nothing is recorded; the
  [snapshot](#late-joiners) covers load-time state.

### Applying it on a client

In the apply scope, in order. An entry that echoes one of this client's own predicted operations is
reconciled rather than applied again: the provisional UID is replaced by the server's, and if the state
after the entry differs from the prediction, the client resyncs the car. Otherwise:

- `Installed`: the item is known and inactive (else wait for its Create, below). Release it from any
  avatar's hand, `AssignUID(uid)`, `GadgetItem.Place(anchor, pose, item, null)`, then
  `Gadget.SaveDataLoaded(gadgetData)`. On a freshly placed projection that is exactly vanilla's load,
  so it restores settings, soldering, mount point states, on-glass flags and the wires whose partners
  exist. On the requesting game, `Place` also drops the item from the player's hand, which is the install.
- `Removed`: `GadgetBase.Remove` (or `ForceRemove` when forced), reproducing holes, unwiring and
  unmounting. The item's new placement follows as an item update.
- The rest: the native call with the entry's values.

Each entry is idempotent by revision: an entry at or below the anchor's applied revision is skipped. An
entry whose anchor isn't spawned yet, or whose item hasn't arrived, waits in a per-anchor queue that
keeps the order; a queue older than 10 s, or an entry that fails (the native call returns false, or
the state afterwards isn't what the entry says), asks for a [resync](#desync-and-rollback).

## Gadget state

Settings and running state are typed fields per gadget class, kept by small adapters in
`Shared/Gadgets/State/` (one file per family). An adapter knows its fields, the vanilla setter for
each, and the LOD's `SyncControls()` to move the physical knob afterwards. Fields use vanilla's save
keys, so a snapshot and a live update say the same thing.

| Family | Fields | Kind |
| --- | --- | --- |
| Switch | `value` | setting |
| Alternating controller | `value` (interval), `order`; running: `alternatorState` | setting, running (on change, ≤ 4 Hz) |
| Switch setter | `mode`, `dir`, `sideCorrection` | setting |
| Amp limiter | `mode` | setting |
| Overheat protection | `mode`, `cut`; running: reached-limit | setting, running |
| ATS | `mode`; running: `currentTimer` | setting, running (1 Hz and on reset) |
| Roadrunner | `target`; running: `countup` | setting, running (1 Hz and on start/ack) |
| Anti-slip | running: activated | running |
| DPU | `regime`, `channel`, `reverse` | setting |
| Proximity screen | `channel`, `mode` | setting |
| Control panel | `tilt` | setting |
| Defect detector | running: `snoozed` | running (on change) |
| Hanger | running: swing angle and velocity | running (10 Hz while swinging) |
| Text | `text` | setting |

Commands have no state of their own: ATS acknowledge, roadrunner start and acknowledge, the defect
detector's snooze, the control panel's joysticks, horn, starter and power-off. A client shows the
effect at once and sends them; the server runs the vanilla method; the result shows in state or in
train controls, and corrects the client if it differs.

The defect detector keeps its snooze in its LOD, which a dedicated host may never load. Its adapter
keeps `snoozed` on the server instead, with vanilla's rules: set by the snooze command while there is a
defect, cleared by a new derailment or overheat and when the defect clears (worked out on the server
from its own trains). Every game shows its lamp from that field and the defect, which each game derives
from the synced derail and brake-heat state.

- **The server watches.** Each server tick it reads the adapters of installed gadgets and sends what
  changed in a `ClientboundGadgetStatePacket` (item, state revision, fields, the operation id of the
  request that caused it), running fields at their rate. This catches the host player's knobs, server
  logic and applied requests alike.
- **A client's setting is predicted.** A remote client lets the vanilla setter run (the knob moves and
  outputs update at once) and sends `Setting` with an operation id, under the knob's lease when it was
  grabbed ([Optimistic](#optimistic)). The server applies it in the apply scope (setter, then
  `SyncControls`) after checking the lease, range (5 m) and the value's bounds, and its broadcast
  echoes the id. The host's player turns the server's own gadget, so its knobs aren't
  requests; the watch broadcasts them, as the host's cab levers commit directly.
- **Modded gadgets** with no adapter get correct state from snapshots (install, join, resync) but no
  live settings.

`GadgetSwitchPatch` is deleted when switches move to this.

### Shared motion and randomness

Vanilla drives some gadgets from local time, local physics or `Random`, so two games show different
things. Each gets the cheapest form that is the same everywhere:

- **Beacon spin, oscillating light sweep:** their phase is computed from the shared network tick clock
  and the gadget's UID instead of accumulated `Time.deltaTime`, so every game shows the same angle with
  no traffic.
- **The control stand's rain fault:** vanilla rolls `Random.value < rain` every 0.125 s. Every game
  rolls instead from a hash of the shared tick (in 0.125 s steps) and the gadget's UID against the
  synced weather's rain, so the panel faults at the same moments everywhere and blocks everyone's
  input together. Whether the weather sync gives every game the same rain value is checked in its PR;
  if not, the server rolls and sends the fault state.
- **Hanger swing:** a pendulum driven by the car's motion, with a random kick when an item is hung.
  The server runs it and sends the angle and angular velocity at 10 Hz while it swings (as running
  state); clients interpolate and never run their own. The forced unsnap is the server's alone.
- **Alternating controller:** server-run, its state sent on change (above).

## Logic that writes the train

Only the server runs gadget code that writes a train (research:
[How gadgets write the train](../research/DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md#how-gadgets-write-the-train)).
On a client, `Controls.Move` with the cab loaded scrolls the player's lever, which the mod sends as
the player's input, so client-run gadget logic would apply twice.

- **Off on remote clients:** `GadgetAmpLimiter`'s port writes, `GadgetAntiSlip.Update`,
  `GadgetOverheatProtection.Update`, `GadgetATS.Update`, `GadgetRoadrunner.Update`,
  `AlternatingController.Update` (`Client/Gadgets/GadgetServerOnlyPatches.cs`). Their displays read
  the replicated running state.
- **Server writes go through the server's simulation commits.** A control a gadget moves is committed
  and broadcast like any server-side control change. **The gadget always wins** (decided 2026-10-21):
  if a player holds that control's lease, the server revokes it first, which is vanilla's
  `TryUnhandControl` for that player: their game lets go of the lever. A gadget behaves as part of the
  train and follows the train's sync.
- **Steps without an interior.** With no cab loaded (always on a dedicated host, and on a listen host
  for cabs its player isn't in), `Move` on a non-notched control jumps to 0 or 1. On the server, a
  gadget's `Move` on a control with no loaded interior steps by the cab lever's scroll step, read from
  the livery's interior prefab and cached; anti-slip's notch count comes from the same place.
- **DPU.** The server applies channel and regime. Clients apply the same channel membership so their
  controls show blocked and the lamps are right; control values on receivers come from the server's
  ports. Whether this composes with the mod's MU cable propagation is checked in its PR.
- **ATS** stays inactive while a remote controller is in control of the loco
  (`IsActivelyControlled`). The server has to answer that for a player's remote; how it learns that
  (the remote's control lease is the likely source) is settled in its PR.
- **Control panel.** Joystick steps and buttons are commands applied as control inputs on the server,
  on the car the panel is installed in, through the same lease rules as a cab lever. The rain fault is
  shared ([above](#shared-motion-and-randomness)), and the server ignores panel commands while it
  holds.

## Items that gadgets create and destroy

Vanilla creates and destroys items in the middle of gadget actions. On a remote client those paths are
switched off, and the server reproduces them through its item lifecycle, so items only ever get NetIds
from the server.

| Vanilla | On the server | Remote clients |
| --- | --- | --- |
| Duct tape's last use: destroy the tape, equip a new empty tape | `MountPoint` with a tape item: the server spends a use (`uses` becomes a server-authoritative tracked value); at 0 it destroys the tape and spawns an empty tape in the requester's hand | `ConsumeOneUse` off |
| Solder spool's first use: destroy it, put an empty spool in the tool's magazine | the server's `Solder` handler debits the tool (`AMMO`, server-authoritative), swaps the spool in the server's copy of the tool's magazine | the swap in `ProcessSoldering` off; loading and ejecting spools are item-container moves as today |
| Label maker: instantiate a new gadget item and install it; destroy it on cancel | `SpawnInstall`: spawn the item, install it, set the text | the label maker places nothing: it opens its text popup at the chosen pose and sends `SpawnInstall` when the popup closes with text. Vanilla opens the popup from the placed gadget, so how it opens without one is settled in that PR; placing a local copy would link it and allocate a UID |
| `DestroyOnGadgetUnlink`: destroy the label's item, spawn the replacement | runs natively inside `Remove`; the item code broadcasts the destroy and the new item | off |

## Tool use other players see

Everyone near a player using a gadget tool sees and hears it, as they would in one game.

- **One-shot sounds** come with the change: every game plays vanilla's place, remove, untape, fill and
  wire sounds at the gadget when it applies the change-log entry, not only the actor's.
- **Ongoing work** (the drill's sound and particles, taping, the soldering gun's sparks, sound and
  lamp, the hammer's swing, the crimping tool's loop) is a tool-use presence: the acting player's
  game sends `ServerboundGadgetToolUsePacket` when work on a target starts and stops, the server relays
  it to players within hearing range of the target, and their games run the tool's own effects
  (`DrillEffects`, the soldering particles and audio, the work animation) on that player's held tool,
  aimed at the target. The server drops a presence whose player stops holding the tool or leaves.
- The **progress** of the work stays the actor's, as it is in vanilla: only its result is a change.

## Forced removals

| Trigger | Server | Remote clients |
| --- | --- | --- |
| Car deleted | native: each gadget force-removed; the server then sends each owned gadget item straight to its owner's Lost and Found (online or not), as vanilla's `RespawnOnDrop` does for owned items far from the player (the manual's "return to a tool shed"); unowned ones stay in the world at the gadget's pose. The change log records the removals | runs natively when the client applies the car's despawn, in the apply scope; the `Removed` entries are then already applied and skipped |
| Window glass broken | native, from the server's damage | runs natively when the client applies `ClientboundWindowsBrokenPacket`, in the apply scope |
| Collision dislodge (random) | native: the server's roll is the only roll | `TrainCarCustomization.OnCollisionEnter` off |
| Hanger flips | native | `Dangler`'s forced unsnap off |
| Mount host removed | native cascade | reproduced by the `Removed` entries in order |

A pooled car keeps its holes in vanilla. The client already replaces a car's holes from its spawn
(`NetworkedCarSpawner.SetCustomizations`); the server clears them when a car returns to its pool.

## Persistence

The server keeps the vanilla save as its store (decided 2026-10-19). Gadgets add no records of their
own.

- **Installed gadgets** stay in vanilla's `Storage_InstalledGadgets` with `placedOn`, pose and
  `gadgetData`, which already carry UIDs, wires, soldering, mount points, mounts and settings. **Holes**
  stay with the car (`modCarState`) and the world (`"Customizers"`).
- **Registration.** When the item registry first sees an item that is in `StorageInstalledGadgets`
  (the vanilla load, or later), it registers it as `Installed` on its anchor, never `World`, and never
  indexes its inactive transform.
- **One save owner.** `WorldItemsV1` skips `Installed` records (it already would, once they are
  `Installed`), and the save-time dedupe strips installed items as it strips world and inventory ones.
- **The owner.** An item's persistent owner lives in the mod's records, which `Installed` items are
  not in. `PersistentWorldItemIdentityPatch` adds the owner's GUID to the item's vanilla save data
  (`dvmpPersistentOwner`) beside the persistent id, and restores it on load.
- **A missing anchor on load.** Vanilla sends the item to its Lost and Found, which
  `StorageControllerAddLostItemPatch` blocks for owned items. Owned: the owner's Lost and Found with
  `LostItemReason.GadgetDestinationMissing`, once ownership is by identity (owned-items PRs 2 and 3);
  until then vanilla's path. Unowned: vanilla's behaviour, as for other unowned items.
- **Car deleted:** owned gadget items go straight to their owners' Lost and Found. Its records
  carry the owner's identity; PR 3a checks that a record can be added while the owner is offline (the
  runtime player number is unbound then) and makes it so if not, so they get it on their next join.
- Clients save nothing, as for everything else.

## Dedicated hosts

A dedicated host is the server with no local player. It loads and saves gadgets through vanilla like
a player host, and it runs the gadget logic. What differs:

- **No local intents.** Nothing intercepts anything; every request comes from a remote player.
- **Placement colliders forced on** for the target car while the server runs vanilla's checks; see
  [What the server checks](#what-the-server-checks).
- **No cab interiors.** Gadgets parent to `TrainCar.interior`, which always exists, and bind to the
  sim's controls overrider. `Move` gets the [step emulation](#logic-that-writes-the-train).
- **No camera.** `Dangler.OnSnap` reads `PlayerManager.ActiveCamera`; a server patch skips the camera
  part when there is none. `HoleFiller` and the HUD and popup code run only for a player.
- **LODs** may load where the server's distance LOD follows a player. Nothing the server owns lives
  in a LOD: the defect detector's snooze moves to its adapter, and the switch setter's junction
  lookup is derived in each game from the synced train and junction states, so every game finds the
  same junction. The switch setter's change button switches a junction, which `NetworkedJunction`
  already sends.
- **`GadgetColliderHolder`** decides from `PlayerManager.PlayerTransform` when the placement
  colliders are on; the server switches them on itself for its checks.

The scenarios run every gadget PR on a dedicated host as well as a player host.

## Late joiners

`PlayerLoadingState.ReadyForCustomizers` is reserved between `ReadyForTrainSets` and
`ReadyForItems`; the client has a `TODO` slot for it and the server would kick a client that sent it.
Gadgets use it:

1. The client reaches `ReadyForCustomizers` after the train baseline, so every car exists.
2. The server sends a Create (placement `Installed`, with its car) for every installed item, then one
   `ClientboundGadgetJoinSnapshotPacket`: for each car with gadgets, each gadget's item, its UID, and
   its item's whole vanilla save data (`ItemSaveData.SaveItemData()`, the mod's own `dvmp*` keys left
   out), which holds the pose, `placedOn` and `gadgetData`. It is sent even with no gadgets, so the
   client knows the step is done. Car holes come with the train baseline (`TrainsetSpawnPart`); the
   resync of 3a sends them with a car's snapshot.
3. The client replays vanilla's load of that data (`StartingItemsController`): for every gadget,
   `AssignUID` (the server's, before `Place` links it, so the client allocates none), then
   `ItemSaveData.LoadItemData`, which places it and restores its state; then every item's
   `PostLoadItemData` (mounts, the alternating order, text, hanger snaps). It sets
   `GadgetItem.Item` first, which vanilla sets in `Start` and an inactive item never runs. Wiring
   tolerates any order because both ends carry the edge. Because the whole item save data is
   replayed, modded gadgets come through too.
4. The client advances to `ReadyForItems`. The server sends live gadget traffic only to players past
   `ReadyForCustomizers`, so nothing in the log predates the snapshot.

## Desync and rollback

The server is never rolled back. Clients converge on it.

- **Structural changes** are predicted, and a rejection undoes them as the [Optimistic](#optimistic)
  table says, newest first, with the reason shown. A removal is undone by resyncing the car, since only
  the server's snapshot still has the gadget's UID, soldering and wires.
- **Settings** are predicted. The server's broadcast is the truth: the requester skips the echo of its
  latest pending operation for that field and applies anything else, including the correction a
  rejection sends. The knob snaps back through `SyncControls`.
- <a id="soldering"></a>**Soldering** is predicted: the soldering player's game runs `MakeSoldered`
  with its own units at once (so the gadget powers up when it reaches 65536) and sends them. While it
  holds the iron on a gadget it shows the larger of its prediction and the server's units; after, it
  sets the server's (`SetSolderingUnits`).
- **Resync.** A gap in an anchor's revisions, a queued entry older than 10 s, an entry that fails, or a
  client allocating a UID sends `ServerboundGadgetResyncRequestPacket` (anchor, applied revision). The
  server answers with the anchor's snapshot. The client rebuilds that anchor: for each local gadget
  not in the snapshot, or with a different UID or pose, set its mount points to `None` (so unlinking
  makes no holes) and `Remove` it in the apply scope; replace the holes; place the missing gadgets as
  at join; apply the state of the rest. At most one resync per anchor is outstanding.
- **Detection.** `dvmp_desync` gains a gadget digest per anchor: gadgets (item, UID, pose to 1 cm,
  powered, soldered units), mount point states, mounts, wire edges, hole count, and adapter fields.
  Host and client must match after every scenario step that changes gadgets.

## Packets

| Packet | Direction | Contents |
| --- | --- | --- |
| `ServerboundGadgetRequestPacket` | client → server | operation id, a `GadgetRequest` (kind and its fields, per [Requests](#requests)) |
| `ServerboundGadgetSettingPacket` | client → server | operation id, item, fields |
| `ServerboundGadgetCommandPacket` | client → server | operation id, item, command, argument |
| `ServerboundGadgetResyncRequestPacket` | client → server | anchor, applied revision |
| `ClientboundGadgetChangesPacket` | server → clients | anchor, first revision, `GadgetChange[]` with operation ids |
| `ClientboundGadgetRequestResultPacket` | server → requester | operation id, rejected, reason |
| `ClientboundGadgetJoinSnapshotPacket` (built, PR 2) | server → joining client | per car, `InstalledGadgetData[]`: item, UID, the item's vanilla save data as JSON |
| `ClientboundGadgetAnchorSnapshotPacket` (3a) | server → client | one car's resync: revision, holes, `InstalledGadgetData[]` |
| `ClientboundGadgetStatePacket` | server → clients | item, state revision, operation id, fields |
| `ServerboundGadgetToolUsePacket` | client → server | tool item, target (item and mount point, or a hole), started or stopped |
| `ClientboundGadgetToolUsePacket` | server → clients | player, tool item, target, started or stopped |

All reliable ordered except the hanger's swing state, which goes unreliable at its rate. Requests go in `NetworkClient.ObserverMutationPacketTypes` (an observer can't
change gadgets). `gadgetData` is server-made JSON; the server never applies JSON from a client.

## Code layout

| Role | Files |
| --- | --- |
| Core | `Multiplayer.Core/Gadgets/`: `GadgetAnchorModel` (revisions, installed set, wires, holes, idempotent apply, the rebuild plan), `GadgetRequestRules` (the checks that don't need the game: possession, reach, bounds, units), `GadgetChangeLog` (nesting, ordering, flush), `GadgetPredictionLog` (a client's pending operations, their dependencies, reconciling echoes, the undo order), `GadgetStateSync` (setting prediction and echo rules). Tests in `Multiplayer.Core.Tests/Gadgets/`. |
| Shared | `Shared/Gadgets/`: `GadgetApplyScope`, `GadgetAnchorRef` resolution, `GadgetNative` (place with a UID, remove, the on-glass flags), `State/*` adapters, `IGadgetServer`/`IGadgetClient`. `Shared/Protocol/Packets/{Serverbound,Clientbound}/Gadgets/`, `Shared/Protocol/Data/Gadgets/`. Item touchpoints: `Shared/Items/StorageIntegration.cs` (installed storage as a membership). |
| Server | `Server/Gadgets/`: `GadgetServer` (handlers, checks, native apply, item transitions), `GadgetObserverPatches` (the change log), `GadgetSnapshots`, `GadgetStateWatch`, `GadgetControlWriter` (lease revoke, step emulation), `GadgetServerPatches` (dangler camera, pool holes). `Server/Session/NetworkServer.Gadgets.cs` (subscriptions, `RegisterLocalGadgets`, the `ReadyForCustomizers` case). Item touchpoints: `AuthoritativeItemRegistry`, `ServerItemManager.Interest`, `WorldItemPersistence*`, `PersistentWorldItemIdentityPatch`. |
| Client | `Client/Gadgets/`: `GadgetIntentPatches`, `GadgetChangeApplier` (queue, reconcile, resync, rebuild), `GadgetPrediction` (applying and undoing predictions), `GadgetStatePrediction`, `GadgetServerOnlyPatches`, `GadgetItemLifecyclePatches`. `Client/Session/NetworkClient.Gadgets.cs`, `LocalPlayerSession.Gadgets.cs` (results and state the host's player needs). Item touchpoint: the `Installed` apply in `NetworkedItem.Snapshots`/`StateApplication`. |
| Tests | `Multiplayer.Protocol.Tests/GadgetPacketTests.cs` (round trips). |
| Scenarios | `DevTools/DvmpScenarios/Library/Gadgets.cs` (spawn a gadget item, install, remove, drill, tape, wire, solder, set and read state through the vanilla tool methods, the digest); `Scenarios/Gadget*.cs`; runs in `DevTools/Scenarios/gate.sh`. |

## Owned items

Gadgets are bought items, and players install them on trains that everyone shares, so "what stops
someone walking off with an absent player's gadget" came up. The answer is not gadget-specific: every
item a player owns (anything but scene-authored world items) is to be tracked by the server for its
whole life by one id, listed for its owner with where it is, and retrievable from anywhere. That is
its own design (owned items, planned 2026-10-21), together with an all-items Lost and Found change:
an offline owner counts as far away, so their abandoned world items go to their Lost and Found.

What gadget sync owes it:

- the item's persistent id and owner identity survive every gadget transition (install, remove,
  knock-off, car deletion, the label and consumable swaps, which are new items and record what they
  replaced);
- a `Retrieve` removal: the server removes an installed gadget on the owner's behalf and hands the
  item over.

Until it exists, a player can keep someone else's removed gadget; it stays owned by its owner.

On the id itself (discussed with the user 2026-10-21, for that design to settle): keep what exists, a
random GUID for life in the save and server records and the per-session `ushort` NetId on the wire, so
lifelong tracking adds nothing to item packets (Minecraft splits entity ids the same way). Random
GUIDs don't collide in practice; the real risk is one id being copied (a copied save, a dupe, a clone
of an object with its identity), so the guarantee is detecting a duplicate at registration: keep the
first, re-mint the copy, log it. SteamID plus a counter was considered: the server mints ids, not
players; owners change while ids must not; players without Steam have no SteamID.

## Decisions for the user

Decided:

- **Optimistic** (2026-10-21). Every gadget interaction shows at once in the player's game and the
  server confirms or undoes it; only creating or destroying an item waits. Gadget knobs take leases
  like cab levers. See [Optimistic](#optimistic).
- **The gadget always wins** (2026-10-21). A server-run gadget acting on a control takes it from the
  player holding it, as vanilla does.
- **Everything syncs** (2026-10-21). Anything a player can see or hear in the world is the same in
  every game: the defect detector's snooze, the control stand's rain fault, beacon spin, hanger swing,
  other players' tool use. Only a player's own UI is theirs alone.
- **Vehicles only** (2026-10-21). No gadgets on the world, the house, the shed or the paint station;
  see [Off-train installs](#off-train-installs).
- **Anyone may remove anyone's gadget** (2026-10-21), for now. The item keeps its persistent owner, and
  whoever removes it holds it like any picked-up item. **Future: permissions.** An access-control
  system is planned; when it lands, who may install on, remove from, wire, solder or change the
  settings of gadgets on a car becomes its rule. The server's request checks
  ([What the server checks](#what-the-server-checks)) are the one place it plugs in; nothing on the
  client decides it.

## PR plan

Small PRs, in order, each from a branch `gadgets/<what>` off `beta` and into `beta` (the client/server
split merged into `beta` in #276, 2026-10-21).

It runs beside the owned-items work ([owned-items.md](owned-items.md), PR #275), which redoes the item
lifecycle. They meet at four points:

- **Gadget PR 1 goes first.** The ledger counts installed gadgets as one of its stores and its
  migration reads `dvmpPersistentOwner`; both come from it.
- **`AuthoritativeItemRegistry.cs` is shared.** Owned-items PR 1 hooks the ledger into every registry
  commit and moves ownership out of that file; gadget PR 3a adds the install and remove transitions,
  in a file of their own that calls the registry. The two are never in flight at once.
- **Gadget PR 3a after owned-items PR 2** (ownership by identity), so a deleted car's gadgets reach an
  offline owner's Lost and Found.
- Gadget PRs 4, 5 and 9 (tape, spools, labels) come before owned-items PR 7 (lineage), which records
  what they replace; owned-items PR 8 (retrieving installed gadgets) comes after gadget PR 3a.

Each runs its scenarios on a player host and a
dedicated host (`RUNS="<scenario> @player; <scenario> @dedicated"`) and the existing item scenarios it
could affect. Every interaction scenario also runs with the client at 250 ms of lag (`LAG=250`),
checking that the change shows at once in the client's game and that a rejected one is undone. Each
updates this doc's status and, where it changes them, `item-world-sync.md` and the research doc.
From 3b on, every PR predicts its own actions with 3b's machinery.

### 1. Installed items are `Installed`

The item side, with no gadget sync yet: an installed gadget stops leaking as a loose item. Built on
`gadgets/installed-placement`.

- `Server/Gadgets/GadgetItemTransitions`: server patches on vanilla's `AddItemToInstalledGadgets` and
  `MoveFromInstalledGadgetsToWorld` commit the record into `Installed` (with its car) and out again;
  the world index commits a saved installed gadget it meets instead of indexing its stale pose; an
  off-train install found at load is taken off into the world.
- The dead `Removed` item state becomes `Installed`, sent with its car; a client keeps an installed
  item out of hands and inactive; no game reports an installed item as its own observation;
  `ProcessChanged` skips them. `GadgetRemoverPatch` and `wasRemoved` are deleted.
- The owner in vanilla save data (`dvmpPersistentOwner`), restored when the record is made.
- A `WorldItemsV1` row an older build wrote for an installed gadget is ignored.
- Vehicles only, in the player's own game: any other anchor resolves to none, so vanilla's preview
  shows red and a message says why (moved here from 3a; it is small and stops new off-train installs
  at once).
- Core: only a gadget transition moves an item into or out of `Installed`, forced or not; an
  installed item can't be recalled; move reasons `InstalledAsGadget`, `RemovedAsGadget`. Tests.
- Not here: a car missing at load still takes vanilla's Lost and Found path. Sending the gadget to
  its owner's Lost and Found needs ownership by identity (owned-items PR 2, then its Lost and Found
  for everyone, PR 3).
- Scenario **`gadget-save`** (`[Needs(Need.RestartsGames)]`): server steps install two gadgets owned
  by the client, one on a loco and one on the ground, and save in the same frame. The loco's is
  `Installed` on that car, owned by the client, and not loose on the client; after a restart it is
  still installed there, once, owned, not in Lost and Found, not loose on the client; the ground's
  was taken off into the world, owned. Deleting the loco takes its gadget off, still the client's.

### 2. Late joiners see the installed gadgets

- `ReadyForCustomizers` on both sides, item Creates for installed items, the anchor snapshot and its
  apply, `AssignUID` before `Place`. The server-only patches (amp limiter,
  anti-slip, overheat, ATS, roadrunner, alternating, collision dislodge, dangler) go in here, because
  clients now have live gadgets. The gadget digest for `dvmp_desync`.
- Core: `GadgetAnchorModel` (snapshot, apply plan), tests.
- Files: `Core/Gadgets/`, `Shared/Gadgets/`, `Shared/Protocol/.../Gadgets/`, `Server/Gadgets/`
  (`GadgetSnapshots`), `Server/Session/NetworkServer.Gadgets.cs`, `NetworkServer.cs`
  (`ReadyForCustomizers`), `Client/Gadgets/` (`GadgetChangeApplier`, `GadgetServerOnlyPatches`),
  `Client/Session/NetworkClient.cs`, `NetworkClient.Gadgets.cs`, `NetworkClient.World.cs`,
  `DevTools/DvmpScenarios/Library/Gadgets.cs`, the dvmp desync digest.
- Scenario **`gadget-join`**: the host builds, through server steps, a car with a plate bolted at two
  points, a switch mounted on it, a light and a DPU wired to the switch, both soldered, the switch at
  0.5, a taped gadget, and a hole from a removed bolted gadget. The client rejoins (`pair.Rejoin`).
  Host and client digests match: UIDs, wires, soldering, mount points, the mount, holes, the switch
  value, the light's output.

### 3a. Install and remove, confirmed by the server

The plumbing, with every change waiting for the server's answer: useful on its own for testing the
server side, and the base 3b predicts on top of.

- The request path, the checks (vanilla's on the server too), the native apply, the change log
  (`Installed`, `Removed`, `Mounted`, `Unmounted`, nesting, flush, revisions), the per-anchor queue,
  resync, item transitions in and out of `Installed` (in a file of their own that calls the
  registry), completing a hand removal, forced removals on clients through the apply scope, deleted
  cars' gadgets to their owners' Lost and Found, the owner on aim; the place and remove sounds played
  by every game.
- Files: `Core/Gadgets/` (`GadgetRequestRules`, `GadgetChangeLog`), `Server/Gadgets/` (`GadgetServer`,
  `GadgetObserverPatches`, `GadgetItemTransitions`), `Client/Gadgets/` (`GadgetIntentPatches`),
  `Client/Session/LocalPlayerSession.Gadgets.cs`.
- Scenario **`gadget-install`**: the client installs a gadget on a car through `TryPlace` (and, on a
  player host, the host's player installs another); both games show it with the same UID and pose and
  the item `Installed`. The client removes one with the hammer: a world item at the gadget's pose in
  both. The client removes the other by hand: it is in the client's hand in both. The client installs
  on a mounted plate; removing the plate is refused while mounted. A request from 30 m
  (`[ClientOnly]` step) is rejected and the item stays in hand, and so is one clipping into the cab
  wall, by vanilla's own check on the server. The car is deleted with a gadget on it: the item is in
  its owner's Lost and Found at once, digests equal. Installing on the ground is refused in the
  client's own game, with no request sent. `MODE=moving` repeats the install and the hand removal with
  the client riding the car at 10 m/s.

### 3b. Prediction and undo

[Optimistic](#optimistic) install and remove on remote clients, and the machinery the later PRs
predict their own actions with.

- Provisional negative UIDs, applying a prediction in the apply scope, reconciling the echo (the
  server's UID replaces the provisional one; a mismatch resyncs the car), undo newest first, requests
  held while they name a provisional UID, the server rejecting requests that depended on a rejected
  one.
- Files: `Core/Gadgets/GadgetPredictionLog` (with unit tests: dependencies, reconcile, undo order),
  `Client/Gadgets/GadgetPrediction`, `Client/Gadgets/GadgetIntentPatches`, `Client/Gadgets/GadgetChangeApplier`.
- Scenario **`gadget-install`** gains `LAG=250` runs: the install and the hand removal show in the
  client's game before the server answers; when a server step removes a plate while the client's
  install onto it is in flight, the client's predicted install is undone and the item is back in its
  hand; a rejected removal is undone by a resync, with the gadget's UID, soldering and wires back.

### 4. Mount points, holes, tape

- `MountPoint` and `FillHole` requests, `MountPoint`/`HoleAdded`/`HoleRemoved` entries, the tape's
  server-side uses and empty-tape swap.
- Files: `Server/Gadgets/`, `Client/Gadgets/` (`GadgetIntentPatches`, `GadgetItemLifecyclePatches`),
  `Core/Gadgets/`.
- Scenario **`gadget-mounting`**: the client bolts two points of a plate, tapes a third, untapes it;
  removes the bolted plate: two holes in both games; fills one: one hole in both. The client tapes until
  the tape is empty: an empty tape in the client's hand in both games, the full tape gone everywhere.

### 5. Wiring and soldering

- `Wire` and `Solder` requests, `Wired`/`Unwired`/`Soldered` entries, solder prediction, the tool's
  ammo and the spool swap on the server.
- Scenario **`gadget-wiring`**: the client wires a switch to a light and solders both; the light is
  powered in both games; the client unwires it; soldering with `LAG=150` converges to the server's
  units; a fresh spool's first use leaves an empty spool in the tool's magazine in both games and the
  full spool gone.

### 6. Gadget state

- The adapters, `Setting` and `Command` requests, the server's watch, prediction and rollback, the
  HUD path. Switches, alternating controller, switch setter, amp limiter, overheat and ATS settings,
  DPU settings, proximity screen, control panel tilt, the defect detector's shared snooze; beacon
  spin and light sweep from the shared clock; gadget knobs, levers and joysticks taking leases
  through the cab-control path. Delete `GadgetSwitchPatch`.
- Scenario **`gadget-settings`**: the client turns a switch through its knob and through the HUD; the
  wired light follows in both games. A setting out of range (`[ClientOnly]`) is rejected and the knob
  goes back. The amp limiter's mode changes the server's `TractionMotorAmpLimit` port, and the client
  sees the limit. The host's player turns a switch (player host) and the client sees it. A server step
  derails a wagon: the defect detector blinks in both games; the client snoozes it and it is steady in
  both. Two powered beacons show the same angle in both games (within 2°). While the client holds a
  switch's knob, the host's player (player host) can't turn it; with `LAG=250` the client's knob and
  the wired light respond at once in its own game.

### 7. Gadgets that drive the train

- ATS, anti-slip, overheat protection and roadrunner on the server with their running state; ATS and
  roadrunner commands; `GadgetControlWriter` (lease revoke, the step without an interior).
- Scenario **`gadget-ats`**: the client drives a loco with a powered ATS and holds the throttle
  without acknowledging. The server brakes: the client's throttle lease is revoked, the loco stops in
  both games within the expected distance, on a dedicated host too (no interior). Acknowledging resets
  the timer in both games. Scenario **`gadget-antislip`**: the client opens the throttle with anti-slip
  powered while a server step forces wheelslip; the throttle steps down and the sander runs on the
  server, and the client sees both.

### 8. DPU and the control panel

- DPU radio membership on server and clients; control panel joysticks and buttons as control
  commands; the shared rain fault.
- Scenario **`gadget-dpu`**: two locos with DPUs on one channel, transmitter and receiver; the client
  moves the transmitter's throttle; the receiver follows in both games and its controls are blocked
  for the client. Scenario **`gadget-control-panel`**: the client uses the panel's throttle joystick;
  the server's throttle moves by steps. With rain forced on an exposed panel, the fault is on at the
  same ticks in both games.

### 9. Labels

- `SpawnInstall`, text edits and removal, the popup without a placed gadget, `DestroyOnGadgetUnlink`
  on the server; the proximity sensor's knobs and `LabelableItem` text as item tracked values.
- Scenario **`gadget-labels`**: the client labels a car; the label and its text are in both games and the
  label maker left no item of its own; cancelling the popup leaves nothing anywhere; removing the label
  destroys it and spawns its replacement once.

### 10. Hangers and shelves

- Items hung on gadget hangers and put on gadget shelves as `SnappedAttachment` placement keyed by
  `(car, UID)`; the dangler's swing run by the server and sent; its forced unsnap decided by the
  server; the sliding anchor offset sent with the snap.
- Scenario **`gadget-hangers`**: a mug hung on a hanger on a moving car stays hung in both games and
  swings the same way (angle within 5°); the hanger can't be removed while the mug is on it; a forced
  unsnap on the server (a step) drops the mug in both games.

### 11. Tool use other players see

- The tool-use presence: send, relay by hearing range, play the tool's effects on the other player's
  held tool; dropped when the tool is let go or the player leaves. (The one-shot sounds go in with
  each change in PRs 3a to 5.)
- Files: `Client/Gadgets/GadgetToolPresence.cs`, `Server/Gadgets/GadgetServer.cs`, the two packets.
- Scenario **`gadget-tool-use`**: the client drills a mount point and solders a gadget; the host's
  game shows the client's drill effects and soldering sparks running while the client works and
  stopped after; on a dedicated host, a second client sees them.

## Implementation status

2026-10-21: the vanilla research was checked against the decompiled game and the design written.

- **PR 1 built** (#281): installed items are `Installed`; vehicles only. `gadget-save` passes on both
  host kinds.
- **PR 2 built** (`gadgets/join-snapshot`): late joiners get every installed gadget; the server-only
  logic is off on a client for the gadgets the server sent. `gadget-join`, `gadget-save` and `rejoin`
  pass on both host kinds. The comparison the plan gave `dvmp_desync` is a scenario step for now
  (`Gadgets.Digest`: each gadget's own vanilla save data, wires sorted).
- Next: 3a.
