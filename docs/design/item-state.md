# Item state

> Status: designed 2026-10-21, not started. Nothing below is built. It replaces the per-item
> tracked-value patches (`LanternPatch`, `LighterPatch`, `FlashlightItemPatch`) with three generic
> layers, and it leaves installed gadgets to [`gadgets.md`](gadgets.md).
>
> Written before the item refactor ([item-refactor.md](item-refactor.md)), and committed unchanged on
> 2026-10-24 apart from links to docs since moved to `legacy/`. Its place in the refactor isn't
> decided yet. Since then the refactor has made some of it out of date: a stowed item is a row on
> every game, not an object (D14, R4C), and an item's `ItemSaveData` already travels with its
> inventory row (OI-COLD-13) and is planned for clients (OI-MOD-02).

An item's state is what it's doing as opposed to where it is: a lantern's wick and flame, a
flashlight's switch, a boombox's power, volume, station and cassette. Placement (hand, inventory,
world, car) is [`item-lifecycle.md`](../legacy/items/item-lifecycle.md) and
[`item-spatial-physics.md`](../legacy/items/item-spatial-physics.md); this doc is only about state.

Rule for all of it: anything another player can see or hear syncs (decided 2026-10-21, see
[Decisions for the user](gadgets.md#decisions-for-the-user) in the gadget design), and an item
behaves the same in every game whoever holds it.

## Today

State syncs only for the items someone wrote a patch for. Each patch registers tracked values on
the item's `NetworkedItem` (`RegisterTrackedValue(key, getter, setter)`); `LateUpdate` polls them,
and a changed value goes out in the item's `States` (bool, int, uint, float or string).

| Item | Synced | How |
| --- | --- | --- |
| Lantern | wick, flame | `LanternPatch` |
| Lighter | lid, flame | `LighterPatch` |
| Flashlight | switch, battery, intensity, beam colour | `FlashlightItemPatch` |
| Shovel | coal capacity, coal loaded | `ShovelPatch` |
| Banknote | amount (host-authoritative) | `MoneyAmountPatch` |
| Page book (job booklet, manuals) | page | `NetworkedPageBookState` |
| Everything else | nothing | |

Who may send is already generic and stays as it is (see
[Throws and release](items/physics.md#throws-and-release)): only the holder sends an item's state,
or a player working a set-down item's controls inside the 1.5 s interaction window, within 6 m
(`ItemObjectStateAuthority`, `ItemInteractionWindow`, `ItemInteractionPatch`). The host ignores the
rest without a correction.

What's wrong with it:

- **Most items don't sync at all.** The boombox is the playtest case (2026-10-21): it has no
  tracked values, so each game runs its own copy. Players who all switched it on each heard their
  own, on whatever station or track their copy was on. The alarm clock, loco remote, proximity
  sensor, duct tape and labels are the same.
- **A client's copy starts at the prefab's defaults.** The host loads vanilla save data into its own
  copies (world items, cold containers), but a `Create` carries only placement and tracked values, so
  a client never sees a saved state that isn't a tracked value: a boombox left on, an alarm set, a
  remote switched on.
- **Every patch hand-writes the vanilla quirks.** The lighter has two branches for an active and an
  inactive object, the lantern sets its physics knob so the wick doesn't spring back, the flashlight
  needed a save fix for an object that never ran `Start`. Each new item means reading its vanilla
  class and writing another.
- **A stowed item isn't polled.** DV deactivates items in the inventory, and `LateUpdate` doesn't
  run on an inactive object. State can still change while stowed: the boombox keeps playing from a
  separate `noDisable` object and moves on to the next track, and the alarm clock's clock runs and
  can clear its alarm. None of that is seen until the item is next active.

## What the game gives us

**Vanilla already says what every item's state is.** Each stateful item class subscribes to
`ItemSaveData.ItemSaveDataRequested` and writes its state as JSON under its own keys, and loads it
back from `ItemSaveDataLoaded` (and `AfterContainerSaveDataLoaded` for items whose state depends on
what's inside them). Seventeen classes do: listed in [Per-item notes](#per-item-notes).
`ItemSaveData.SaveItemData()` returns the whole object; `LoadItemData(data)` and `PostLoadItemData()`
put it back.

**But loading assumes a fresh object.** The load handlers are written for an item that was just
spawned in its default state, one frame after instantiation (`StartingItemsController` waits that
frame; so does `NetworkedColdContainerManager`). Many only switch things on:

- Boombox: `cassettePlaying = true` calls `Play()`, `false` does nothing; loading also unhooks its knob
  listeners for over a second (`SyncInteractablesCoro`).
- Loco remote: `Item_turned_on_state = true` powers it on, `false` does nothing.
- Alarm clock: a set alarm plays the tick sound on every load.
- Flashlight: refuses to load before it's initialised and reloads its battery.

So loading is right for a new copy and wrong for a live one. That splits the design: the save data
is used whole only when a copy is created, and live changes go through setters that are safe to call
again.

Two more things about `ItemSaveData`:

- **`SaveItemData()` returns one cached `JObject` per item.** Every save, the handlers write into the
  same object, so a key a handler doesn't remove stays from an earlier save (`Money` leaves a stale
  `amount`). Anything the mod sends or edits is a `DeepClone()` of it.
- **Some handlers subscribe late.** The alarm clock subscribes at the end of its first frame, labels
  in `Start`. An object that is instantiated inactive (a copy created straight into another player's
  inventory) runs neither, so a load before its first activation is lost.

**Most player-facing state sits on a control.** Knobs, levers, switches and buttons are
`ControlImplBase`. A player moving one changes its `Value` and fires `ValueChanged`; the item's
vanilla code listens to that (the boombox's power, mode, volume and antenna; the lantern's wick
follows its knob; the proximity sensor's dials). `SetValue(v)` does the same from code: it moves the
control and fires `ValueChanged`, so the item reacts exactly as if a player had turned it.

Momentary buttons are different. The boombox's play, stop, pause, next and previous are
`ToggleSwitchBase` controls whose handlers listen to `Used`, not `ValueChanged`, and
`CassettePlayerButtonController.HandleButtonPress` ignores every second `Used` (one press fires it
twice). Replaying presses would have to reproduce that pairing exactly; syncing the state the press
caused (playing, track) is simpler and self-correcting.

## Design

Three layers. The first two are generic and cover any item, modded ones included; the third is a
short table per item type for what neither of them covers.

### 1. A snapshot when a copy is created

The host puts the item's vanilla save data in every `Create` it sends, and the client loads it into
the new copy before anything else is applied.

- **Host:** a clone of `SaveItemData()` from its copy, with the mod's own keys removed (`dvmpPersistentWorldItemId`,
  `dvmpManagedWorldItem`, `dvmpAuthoredWorldItemKey`), sent as a UTF-8
  JSON string in a new `ItemUpdateData` field present only on `Create`. Interest entry sends `Create`s
  in bulk (a station yard), so the JSON is cached per item and rebuilt only when its object state has
  changed since (the record's object-state revision run). Measure the station-arrival hitch before and
  after: today it's 150-260 ms ([`item-world-sync.md`](../legacy/items/item-world-sync.md)).
- **Client:** `ClientItemManager.CreateItem` instantiates the prefab, waits one frame (as vanilla
  does), calls `LoadItemData` and `PostLoadItemData`, then applies the `Create`'s tracked values on
  top. A scene-authored item the client binds to rather than instantiates is in its scene default,
  so the same load is right for it. A copy that starts inactive keeps its snapshot and loads it one
  frame after it is first active, so late-subscribing handlers are there to receive it.
- **Excluded types,** whose state is synced by something else or whose load does more than set
  state. The list is a table in `Multiplayer.Core`, unit-tested:
  - `ItemContainer`: contents are container storage, and its load re-registers the container id.
  - `SnappableItem`: snapping is placement, and its load calls `SnapItem` again.
  - `GadgetItem`: gadgets; its load calls `Place`, which reparents, drops the item from the
    inventory and registers an installed gadget.
  - `GadgetSolderingTool`: a gadget tool ([Tool use other players see](gadgets.md#tool-use-other-players-see)).
  - `JobBooklet` and `PageBook`: jobs and page state.
  - `Money`: the amount is host-authoritative.
  - `ShopRestocker`: its flag makes destroying the item restock a shop, which only the server does.

This alone fixes the "client starts at defaults" gap for every item, and gives a late joiner, or a
player walking back into range, the item as it is now. It is also the resync: when a copy drifts
beyond repair, the host can destroy and re-`Create` it.

### 2. Controls mirrored

Every value control on an item syncs its `Value`, with no per-item code.

- **Which controls:** every `ControlImplBase` under the item whose nearest `NetworkedItem` is this
  item, except the item's own grab (`ItemInteraction.ItemOf` already makes that cut). Found once,
  when tracked values are registered.
- **Key:** `c:` plus the control's path from the item root (`c:PowerKnob`,
  `c:Knobs/Volume`). Every game instantiates the same prefab, so paths agree; a duplicate path gets
  its sibling index. Modded items with controls are covered the same way.
- **Read:** `control.Value`. A change smaller than 0.01 isn't sent (physics knobs jitter); a value
  at 0 or 1 always is. While the control is moving, at most 10 updates a second, then the final value.
- **Apply:** `control.SetValue(v)`, which moves it and fires `ValueChanged`, so the item's vanilla
  code reacts. Not while the local player is holding that control (the window that already guards
  the lantern's knob).
- **What it doesn't cover:** controls whose effect isn't their value. A momentary button whose
  vanilla listener is `Used` (the boombox's transport buttons, the alarm clock's set button, the loco
  remote's power button) is still mirrored by value, which is harmless since nothing listens to its
  value, and its effect is synced by layer 3. A `SteppedJoint` that reports steps rather than a
  position (the boombox's tuning knob, the alarm clock's time knob) isn't a `ControlImplBase` at all;
  what it changes is also layer 3. A `ButtonBase` that really toggles (the flashlight's switch) does
  fire `ValueChanged`, and is mirrored.

These are ordinary tracked values with generated keys, so the authority rules, the host's relay,
the echo suppression (`TrackedValue` setters mark the applied value as sent) and the wire format are
unchanged.

### 3. Item families: state no control holds

For state that isn't a control's position, each item type gets a family: its vanilla save keys and
a setter per key that is safe to call on a live object. This is the shape the gadget design
already uses for installed gadgets ([Gadget state](gadgets.md#gadget-state), `GadgetSettings`), so
items and gadgets speak the same vocabulary.

- **Read** through the item's own save handler: the family calls `SaveItemData()` and picks out its
  keys, so the value is whatever vanilla would save, however it got there (a knob, the HUD, a song
  ending). No getter is written by hand.
- **Write** with the family's setter for that key. The setter is the only per-item code, and it's
  where each vanilla quirk is handled once.
- **Keys a control already holds aren't listed** (the boombox's `powerOn`, `radioMode`, `vol`,
  `antenna`; the lantern's wick): the control carries them.
- **Polled by the item manager, not `LateUpdate`,** for items the local player holds or carries,
  including stowed ones, at 4 Hz, and on the existing state hooks (grab, release, equip, use). Only
  those items are polled, so the `SaveItemData()` allocation is a handful of items a game.

| Family | Keys | Setter notes |
| --- | --- | --- |
| Boombox | `rsIndex`, `cpIndex`, `cassettePlaying` | Station: vanilla's load only sets the last-played index, so the setter has to retune a playing radio to it. Track: same, for the cassette's playlist player. Playing: `CassettePlay()` or `CassetteStopOrEject()`, both ways |
| Lantern | `On` | Lit or put out, both ways (`Ignite(1)` / `OnFlameExtinguished()`); the wick is its knob |
| Lighter | `Lighter_state` (1 open, 2 lit) | Lid and flame both ways, with today's branches for an active and an inactive object moved here; the lid is scroll and `Used`, not a control |
| Loco remote | `Item_turned_on_state` | `TogglePower(on, playPowerSound: false)` both ways. Pairing (`Paired_loco_ID`) is left out: it controls a loco, and belongs with the trains design if it is synced |
| Alarm clock | `alarmTime`, `alarmSet` | Sets the time and the alarm without the load's sounds, and stops the tick sound when unset. Its clock runs (and can clear `alarmSet`) while stowed |
| Duct tape | `uses` | Symmetric already: `usesLeft` and the tape model |
| Label | `label_text` | Symmetric already: `UpdateText` |

The flashlight and the proximity sensor need no family: the flashlight's `On` is its button and its
battery is unlimited (`FlashlightBatteryDrainPatch`); the sensor's two keys are its two dials.
`FlashlightItemPatch`'s server-authoritative intensity and beam colour stay until the flashlight PR
checks what sets them.

### Song position

Layer 3 syncs which track is playing, not the position within it. Every game starts the track
when it learns of it, so they are a network delay apart, and a copy created mid-song starts that
track from its saved seek position (`lastPlayedSeekPosition`), not from where the holder is now.
Aligning playback to the shared tick clock (as gadgets do for beacon spin) is a later step if it's
noticeable.

### The host's copy and dedicated hosts

The host runs a copy of every item, including those in other players' hands and inventories, and
its snapshot (layer 1) comes from that copy, so the copy has to keep up. It does: it applies the
holder's controls and family keys like any other game. What it must not do is report its own drift
for an item another player holds (`TrackedStateComposer.HostReportsForRemoteHolder` already drops
those).

A dedicated host has no player to hear anything. Its boombox should keep state (for snapshots and
the save) without streaming internet radio or decoding audio: the family's setters run, the audio
source stays muted, and the radio player doesn't start. To decide in the boombox PR whether that's a
patch on `RadioPlayerController` or skipping `PlaylistPlayer` on a dedicated host.

## What this replaces and what stays

- **`LanternPatch`, `LighterPatch`, `FlashlightItemPatch`:** become families (and the lantern's knob
  becomes a mirrored control). Their keys change, which is a protocol change; the protocol
  fingerprint already refuses mixed builds.
- **`MoneyAmountPatch`:** stays. The amount is host-authoritative and money is spent through the host, not
  set by a holder.
- **`NetworkedPageBookState`:** stays. Pages are sent by non-holders by design.
- **Installed gadgets:** [`gadgets.md`](gadgets.md). An installed gadget's controls are on its
  projection, not under the item, so layer 2 never sees them, and `GadgetItem` is excluded from
  layer 1.
- **`ItemObjectStateAuthority` and the interaction window:** unchanged; every layer goes through
  them.

## Per-item notes

From the decompiled game, 2026-10-21. "Live load" is whether calling the load handler again on a
live object moves it to any saved state; it decides nothing above (layer 1 only loads fresh copies),
but it's why layer 3 has its own setters.

| Class | Save keys | Live load | Controls | Stowed |
| --- | --- | --- | --- | --- |
| `BoomboxInteractionController` | `powerOn`, `radioMode`, `vol`, `antenna`, `rsIndex`; with a cassette `cpIndex`, `cassettePlaying` | No: never stops a cassette; `rsIndex`/`cpIndex` only set the last-played index; re-runs `SyncInteractables` (knob input ignored ~1 s, an error if one is running); "Unhandled trigger" warnings when mode or power is unchanged | Power, mode and volume knobs, antenna lever; tuning is a `SteppedJoint` (steps); transport buttons are `Used` | Audio and the cassette/radio state run on `noDisable` and keep playing; knob sync waits for `OnEnable` |
| `Lantern` | `On`, `Wick_state` (absent when ≥ 0.99) | No: never puts the flame out | Wick knob | Knob value applied on `OnEnable` |
| `Lighter` | `Lighter_state` bitmask | No: never closes or puts out | None (scroll and `Used`) | Open animation not replayed on enable |
| `FlashlightItem` | `On`, `Battery_power`, `Battery_depleted` | No: never switches off; an error before initialisation | Switch (`ButtonBase`) | Toggle ignored while inactive, applied on `OnEnable` |
| `LocomotiveRemoteController` | `Item_turned_on_state`, `Paired_loco_ID`, battery | No: never powers off or unpairs; re-pairs on every load | Power, pairing, coupling buttons (`Used`), selector `SteppedJoint`, joysticks; none saved | Stops transmitting while inactive |
| `AnalogAlarmClock` | `alarmTime`, `alarmSet` | Mostly: time always applied; unset doesn't stop the tick; plays sounds every load | Time `SteppedJoint`, set button (`Used`); none value-mirrored | Clock keeps running; tick sound stops on disable, replays on enable |
| `ProximitySensor` | `channelKnob`, `rangeKnob` | Yes, through the knobs | Channel and range dials | Deferred to `OnEnable` |
| `DuctTape` | `uses` | Yes | None | No effect |
| `LabelableItem` | `label_text` | Yes (subscribes in `Start`) | None | No effect |
| `Money` | `amount` | Yes; stale key left in the cache | None | No effect |
| `PageBook` | `BookletPageNumber` | No: never resets to page 0; flips pages in a coroutine | None | Page animations end on disable |
| `JobBooklet` | `jobId` | No: stores the id only; an error when absent | None | No effect |
| `ItemContainer` | `ContainerId` | No: re-registers the container | None | No effect |
| `SnappableItem` | coupler or hanger car id and key | No: never unsnaps, snaps again | None | No effect |
| `GadgetItem` | `gadgetData`, `placedOn`, pose | No: never unplaces, places again (reparent, inventory purge) | On the gadget, not the item | Gadget is a separate object |
| `GadgetSolderingTool` | `AMMO` | Yes (after container load) | None | No effect |
| `ShopRestocker` | `Restock` | Yes; makes a later destroy restock | None | No effect |

## Testing

A new scenario, `item-state`, run on a player host and a dedicated host (`PAIRS="p:1 d:1"`):

- A client creates a copy of a boombox the host left on in radio mode at station 3, volume 0.4:
  the client's copy is on, in radio mode, on station 3 at 0.4 (layer 1).
- The host turns the boombox's volume and switches it to cassette; the client's knobs and mode follow
  (layer 2).
- The client holds the boombox with a cassette in, presses play and stows it; the host's copy plays,
  and its track follows the client's when a song ends while stowed. The client takes it out and
  presses stop; the host's copy stops (layer 3, stowed polling).
- The lantern's existing checks (`item-release`, `MODE=interact`) pass unchanged on the generic
  path.

## PR plan

1. **Snapshot on create.** The `Create` field, the host's cache, the client's one-frame load, the
   exclusion table and its tests, the hitch measurement. Fixes defaults for every item on its own.
2. **Controls mirrored,** and the boombox's knobs with them. Stowed-item polling in the item manager.
3. **Families,** with the boombox (station, track, playing) as the first and the dedicated host's
   muted audio.
4. **Lantern, lighter and flashlight onto the generic path,** their patches deleted, their scenarios
   rerun.
5. **The remaining families** (loco remote, alarm clock, duct tape, label), one each. The proximity
   sensor is done by PR 2.

## Decisions for the user

- **Family keys are vanilla's save keys,** as the gadget design chose. Recommended: one vocabulary
  for snapshots and live updates, items and gadgets alike.
- **Song position isn't aligned** in the first version. Recommended; revisit if players notice.
- **A dedicated host never plays audio.** Recommended.

## Implementation status

2026-10-21: designed from the decompiled game (`BoomboxInteractionController`,
`CassettePlayerController`, `CassettePlayerButtonController`, `ControlImplBase`, and the seventeen
save handlers). Nothing built.
