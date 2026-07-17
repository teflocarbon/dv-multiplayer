# Derail Valley Interaction Logic

> Status: active dnSpy research for the debug-only multiplayer runtime test harness. This document
> records Derail Valley gameplay entry points; it does not describe a replacement interaction
> system.

## Purpose

Runtime integration tests must invoke the same post-input gameplay methods used by Derail Valley.
Directly parenting an item to a hand, changing an inventory array, or calling a multiplayer state
machine would bypass interaction validation and presentation side effects and could produce false
confidence.

This document maps physical desktop input to the earliest practical gameplay method that the test
harness can invoke without synthesizing keyboard or mouse events.

The governing test rule is:

> Arrange may use controlled internal state manipulation. Act must enter through the same gameplay
> boundary used by a real player. Assert must be read-only.

## Scope

Initial research covers desktop interaction:

- hover and raycast selection;
- pickup, holding, dragging, release, and throw;
- item use and use targets;
- inventory equip, drag, and quick-move;
- container access;
- physical button use;
- safe test-only player teleportation.

VR-specific physical grabber, hand, raycast, and controller behavior remains a manual acceptance
surface until a reliable simulated VR runtime is available. Shared authority and post-interaction
behavior can still be exercised through desktop tests.

## Known assembly/type map

| Area | Type | Assembly | Current knowledge |
| --- | --- | --- | --- |
| Grab state machine | `DV.Interaction.Grabber` | `DV.Interaction.dll` | Captured; owns confirmed idle/holding/dragging transitions and grab start/stop ordering |
| Desktop interaction requests | `DV.Interaction.GrabberInteractionHandlerDV` | `Assembly-CSharp.dll` | Captured; converts requests into state-dependent `Grabber.Trigger` transitions |
| Physical desktop input | `DV.Interaction.GrabberInputHandler` | `DV.Interaction.dll` | Captured; maps primary press/release and drop into interaction requests |
| Hover/raycast | `DV.Interaction.GrabberRaycasterDV` | `Assembly-CSharp.dll` | Captured; performs the real four-metre raycast and hover lifecycle |
| Generic grabbable | `DV.Interaction.AGrabHandler` | `DV.Interaction.dll` | Captured; owns grabbed identity and lifecycle events, but not permission validation |
| Item grabbable | `DV.Interaction.GrabHandlerItem` | `DV.Interaction.dll` | Captured; item attach, physics, release, use, and throw implementation |
| Non-VR item bridge | `DV.CabControls.NonVR.ItemNonVR` | `Assembly-CSharp.dll` | Captured; connects item grab state to `ItemBase` and collider presentation |
| Grab/inventory bridge | `DV.InventorySystem.GrabberStashingHandler` | `Assembly-CSharp.dll` | Captured; converts grab events to equip/unequip and inventory equip to force-hold |
| Desktop inventory behavior | `DV.InventorySystem.InventoryViewNonVR` | `Assembly-CSharp.dll` | Captured; handles quick equip and quick move after input interpretation |
| Inventory transaction controller | `DV.UI.Inventory.InventoryUIController` | `Assembly-CSharp.dll` | Full class already captured |
| Legacy item-use abstraction | `DV.Items.ItemUseNonVR` | `Assembly-CSharp.dll` | Captured; no shipped subtypes or active users |
| Item use contract | `DV.Interaction.IItemUse` | `Assembly-CSharp.dll` | Hover/use compatibility and handling contract |
| Use target | `DV.Interaction.ItemUseTarget` | `Assembly-CSharp.dll` | Target component passed to item-use handlers |
| Physical button | `DV.CabControls.ButtonBase` | `Assembly-CSharp.dll` | Captured; `Use()` is the confirmed logical gameplay boundary |
| Player teleport facade | `PlayerManager` | `Assembly-CSharp.dll` | Exposes shared `TeleportPlayer` overload and teleport events |
| Non-VR teleport | `PlayerTeleportNonVR` | `Assembly-CSharp.dll` | Desktop implementation behind the player teleport facade |

## Already captured

The following relevant decompiled sources or production paths are already available and do not need
to be fetched again:

- full `Inventory`;
- full `AItemContainer` and `ItemContainer`;
- full `ItemContainerRegistry`;
- full `InventoryUIController`;
- full `InventoryViewVR`;
- inventory providers and slot display/controller logic;
- storage and Lost-and-Found methods;
- page and booklet interaction paths;
- job booklet/report use examples;
- cold-container UI interception paths;
- exact `InventoryViewNonVR.QuickMoveAction` IL;
- multiplayer patches observing `GrabHandlerItem.Throw` and recovering stale `Grabber` state.

The first and second interaction fetches additionally captured:

- `AGrabHandler` (provided twice with identical contents);
- `GrabberInteractionHandlerDV`;
- `GrabberInputHandler`;
- `GrabHandlerItem`;
- `GrabberRaycasterDV`;
- `ItemNonVR`;
- `InventoryViewBase`;
- `InventoryViewNonVR`;
- `GrabberStashingHandler`.

The full `DV.Interaction.Grabber` class is captured and its pickup/drop ordering and completion
signals are documented below.

## Confirmed quick-move behavior

`InventoryViewNonVR.QuickMoveAction(int index, bool isHandSlot, bool isContainerSlot)` is the real
desktop quick-transfer action after input interpretation.

When moving an inventory item into an active Derail Valley container, vanilla code performs:

```text
validate quick-move source
resolve active container and destination slot
Inventory.DropItemFromHandsOrInventory(index, true)
activeContainer.AddItem(item, destinationSlot)
```

The destructive removal occurs before `AddItem`. This is why a late cold-container interception
produced a dropped or falling client representation. The multiplayer patch now intercepts
`QuickMoveAction` itself and converts it into one host-authoritative cold-container request.

This method is a known `UIController`-fidelity runtime test boundary.

## Findings from the first and second fetches

### Physical desktop input dispatch

`GrabberInputHandler.Update()` executes after `GrabberRaycasterDV.Update()` and performs:

```text
every frame
  Grabber.DoUpdate()

InteractionPrimary pressed edge
  IGrabberInteractionHandler.RequestStartInteraction()

InteractionPrimary released edge
  IGrabberInteractionHandler.RequestEndInteraction()

Drop pressed edge while holding
  IGrabberInteractionHandler.RequestDrop()
  currentItemHeld.Throw(playerRig.attachPoint.forward)
```

The throw ordering is important: the state-machine drop request is issued synchronously before the
throw force is applied. A faithful automated throw must use the same pair and ordering. Calling
`GrabHandlerItem.Throw` alone applies force without releasing or updating the grabber and inventory.

No keyboard/mouse synthesis is required. The test driver can invoke the request methods directly
after arranging the same runtime preconditions.

### Raycast and hover selection

`GrabberRaycasterDV` is marked to execute before `GrabberInputHandler`. Its normal update calls:

```text
UpdateRaycast
  cursor.GetRay()
  GetRaycastedGrabHandler(ray)
  choose CurrentlyDragged or CurrentlyRaycasted as desired hover
  fire UnHovered/Hovered transitions
```

The raycast:

- searches up to four metres using `sphereCastMask` and trigger collisions;
- skips the train-interior layer through the generic gadget-depth filter;
- supports explicit `GrabberRaycastPassThrough` colliders;
- resolves `StaticInteractionArea.grabHandler` and registered `interactionColliders`;
- respects per-handler `InteractionPassThrough(hitPoint)` delegates;
- stops at ordinary non-trigger obstructions;
- blocks selection while the hotbar is open;
- blocks selection for an unlocked/invisible cursor state and pointer-over-UI state;
- restricts hovering while an item is held unless screenspace mouse mode and the item's
  `isHoverableWhileHeld` policy allow it;
- falls back from a distant non-item interaction to an enclosing `GrabHandlerItem` beyond 1.5 m.

`ReleaseHover()` clears the current hit and emits the normal unhover lifecycle.

A world-pickup runtime test should place the item within the real ray, call/await
`UpdateRaycast()`, assert that `CurrentlyRaycasted` is the intended handler, and only then invoke
`RequestStartInteraction()`. Writing the private `CurrentlyRaycasted` property directly would
bypass obstruction, range, pass-through, UI, and hover behavior and should not be the default
driver.

### Idle pickup request validation

`GrabberInteractionHandlerDV.RequestStartInteraction()` only fires an event. The `Grabber` state
machine selects a state-specific callback; while idle that callback is `IdleStartInteraction()`.

`IdleStartInteraction()` retains these checks:

- the global blockers canvas must not be active;
- a handler must currently be selected by the raycaster;
- only one dragged handler may exist;
- non-item draggable interactions require the `WorldInteraction` feature flag;
- item holding requires screenspace mouse mode to be off;
- the target must report `IsItem`;
- no item may already be held;
- the `ItemGrab` feature flag must be enabled.

It returns `Grabber.Trigger.Drag`, `Grabber.Trigger.Hold`, or no transition. It does not itself call
`StartInteraction`.

The captured code does not check `AGrabHandler.interactionAllowed`, `mustHoldButton`, or
`AllowPickupAndThrow` in `IdleStartInteraction`, the raycaster, or the now-captured `Grabber` state
machine. Those checks, if present, are in another caller or handler implementation. This remains a
research target, but it no longer blocks confirmation of the state-machine ordering.

### Confirmed `Grabber` state machine

`Grabber.Awake()` constructs a Stateless hierarchical state machine with four states:

```text
Idle
  StartInteraction -> ask IdleStartInteraction
  Hold             -> Holding
  ForceHold        -> Holding
  Drag             -> Dragging

Holding (substate of Idle)
  StartInteraction -> ask HoldingStartInteraction
  EndInteraction   -> ask HoldingStopInteraction
  Drag             -> DraggingWhileHeld
  Release          -> Idle

Dragging (substate of Idle)
  Update           -> OnDragUpdate
  EndInteraction   -> Idle

DraggingWhileHeld (substate of Holding)
  Update           -> OnDragUpdate
  EndInteraction   -> Holding
```

The request methods are event inputs to this machine. The interaction handler does not mutate the
state directly: its state-specific callback returns an optional trigger, and `Grabber` fires that
trigger synchronously.

The exact world-pickup ordering is now confirmed:

```text
RequestStartInteraction
  -> StartInteractionRequested
  -> state machine fires StartInteraction while Idle
  -> GrabberInteractionHandlerDV.IdleStartInteraction
  -> state machine fires Hold
  -> OnHoldingEntry
  -> CurrentItemHeld = Raycaster.CurrentlyRaycasted
  -> OnForceHoldingEntry
  -> subscribe EndInteractionForced
  -> handler.StartInteraction(Vector3.zero, grabber)
     -> AGrabHandler.Grabbed
     -> GrabHandlerItem attachment/physics
     -> ItemNonVR.OnGrabbed
  -> Raycaster.UpdateRaycast
  -> Grabber.GrabStarted
     -> GrabberStashingHandler inventory equip
```

The exact ordinary release ordering is:

```text
RequestDrop
  -> DropRequested
  -> ReleaseHolding
  -> if dragging, fire EndInteraction first
  -> if holding, fire Release
  -> Holding.OnExit
  -> HoldingStopInteraction
  -> handler.EndInteraction
     -> AGrabHandler.UnGrabbed
     -> GrabHandlerItem unparent/physics
     -> ItemNonVR.OnUnGrabbed
  -> unsubscribe EndInteractionForced
  -> Raycaster.UpdateRaycast
  -> CurrentItemHeld = null
  -> Grabber.GrabStopped
     -> GrabberStashingHandler inventory unequip
```

`RequestDrop()` is therefore the correct ordinary state-machine release boundary. When the physical
drop button represents a throw, `GrabberInputHandler` calls `Throw` immediately after this entire
synchronous release path completes.

`GrabStarted` and `GrabStopped` are the strongest normal completion events for harness assertions:
they fire after the handler lifecycle and raycast refresh, while preserving the game's synchronous
subscriber ordering. Tests should still verify inventory state after the event because
`GrabberStashingHandler` itself is one of its subscribers.

`ForceEndInteraction()` is also connected correctly: `OnForceHoldingEntry` subscribes
`EndInteractionForced` to `ReleaseHolding`, so a forced handler end drives the grabber back through
its ordinary holding exit. The handler's base end happens before `EndInteractionForced`; the later
holding exit calls `EndInteraction()` again, which safely returns because `grabbedBy` is already
clear.

### AltFuture interaction mock

`GrabberInteractionHandlerMock` is a real test double for `IGrabberInteractionHandler`. It retains
the same request events used by `Grabber`, but replaces production decision logic with a minimal
implementation:

- idle interaction chooses `Drag` or `Hold` solely from the raycasted handler and current grabber
  state;
- it omits blockers, feature flags, screenspace-mouse rules, usability checks, continuous-use
  cancellation, and other production validation;
- holding interaction directly calls `currentItemHeld.Use()`;
- holding stop does nothing;
- force hold retains only the already-holding guard.

This strongly suggests the interaction system was designed for component substitution in internal
tests. It validates our architecture choice to depend on `IGrabberInteractionHandler`,
`IGrabberRaycaster`, and `IGrabberCursor` at narrow test seams.

The runtime multiplayer harness must not replace `GrabberInteractionHandlerDV` with this mock for
gameplay-fidelity scenarios: doing so would make invalid interactions pass. The mock is useful for
isolated state-machine tests or as a reference when building our own debug-only fakes.

### Shipped developer and testing remnants

The shipped assemblies retain several different categories of developer-oriented code. They must
not all be treated as equivalent test infrastructure.

#### Genuine interaction test double

`GrabberInteractionHandlerMock` is the only confirmed interaction mock. dnSpy reports no
`Instantiated By` or `Used By` entries, so its original scene, prefab, or test runner was likely not
shipped. The component remains useful as architectural evidence, but there is no surviving caller
we can reuse directly.

#### Test-scene support

`DV.Testing.TestSceneRig` is an execution-order-controlled scene helper. On startup it disables a
directional light and, when its world mover is active, immediately destroys all `TrainCar` game
objects. It also exposes manual shop and weather toggles.

This is fixture/environment support rather than a test runner: it contains no assertions,
scheduling, result reporting, or isolation protocol. It confirms that AltFuture used dedicated
Unity test scenes with deliberately reduced world state.

`DV.Booklets.Testing` retains a larger collection of booklet fixture/spawner classes. Their value to
our harness is primarily as examples of deterministic data construction and presentation setup.
They do not by themselves establish a reusable general-purpose runner.

`LocoSim.Implementations.Test.SimDataDisplayBase` is an interactive simulation inspector. It
initializes a simulation, subscribes to tick events, samples named ports, and draws an in-game
graph. Despite the `.Test` namespace it is observational developer tooling, not an assertion-based
unit test. Its pattern is still useful for eventual train tests: subscribe to deterministic
simulation ticks and capture a bounded series of port values as a diagnostic attachment.

#### Production VR proxies, not tests

`FakeInteractableObjectProvider`, `TelegrabInteractionHandler.FakeController`, and the fake
controller used by `TelegrabbableGrabbable` are production telegrab adapters. They translate a
remote telegrab target into the ordinary VRTK touch/grab pipeline by creating temporary controller
and interactable representations.

The word `Fake` here describes a presentation/input proxy, not a test double. These objects depend
on live VRTK controller state and should not be mistaken for a headless VR testing facility. They
do, however, prove that DV itself adapts unusual interactions by feeding synthetic representations
through the real downstream VRTK grab path instead of directly mutating item transforms.

#### OS cursor-warp utility

`MousePositionHack.TryWarpCursorPosition(Vector2)` converts Unity bottom-left screen coordinates to
Windows top-left cursor coordinates, compensates for the offset between `Input.mousePosition` and
the operating-system cursor, and calls `User32.SetCursorPos`. Exceptions are logged and reported as
`false`.

This may be useful for a later highest-fidelity desktop UI test tier, but it is deliberately not the
default gameplay driver because it is Windows-only and sensitive to window focus, display scaling,
screen placement, cursor confinement, and concurrent user input. Tests of gameplay methods and
post-input UI controllers should remain deterministic and avoid operating-system cursor movement.

### Force-hold is the inventory path

`RequestForceHold(AGrabHandler)`:

- returns immediately if `Grabber.CurrentItemHeld` is non-null;
- otherwise fires `ForceHoldRequested` with the supplied handler;
- does not perform raycast, range, obstruction, UI blocker, screenspace mouse, or feature-flag
  validation.

Its known callers are `GrabberStashingHandler.HandleEquipChange` and `UnStash`. It is therefore the
correct gameplay boundary for inventory/hotbar equip projection, but not for a faithful world
pickup test.

### Base grab lifecycle

`AGrabHandler.StartInteraction(startWorldPosition, grabbedBy)`:

```text
set grabbedBy
invoke Grabbed subscribers with exception isolation
```

`EndInteraction()`:

```text
return if not grabbed
clear grabbedBy
invoke UnGrabbed subscribers with exception isolation
```

`ForceEndInteraction()` calls the ordinary end path and then emits `EndInteractionForced`.

Consequences:

- `IsGrabbed()` means only `grabbedBy != null`;
- `StartInteraction` itself contains no range, ownership, permission, or feature validation;
- direct calls to `StartInteraction` are too low fidelity for pickup tests;
- `Grabbed`, `UnGrabbed`, and `EndInteractionForced` are useful observable completion events;
- exceptions in ordinary grab/ungrab subscribers are logged but do not unwind the lifecycle call.

`interactionAllowed`, `mustHoldButton`, `interactionColliders`, and pass-through behavior are data
on this base. Only collider and pass-through consumption is confirmed in the raycaster so far.

### Item grab presentation and physics

`GrabHandlerItem.StartInteraction` first runs the base lifecycle, then:

```text
get player rig attach point from Grabber.Cursor.Rig
make rigidbody kinematic
parent item to attach point
apply custom non-VR grab anchor offsets or zero/identity
```

`EndInteraction`:

```text
clear transform parent
run base ungrab lifecycle
make rigidbody non-kinematic
```

`Throw(direction)` performs one default-mode `Rigidbody.AddForce` call with:

```text
direction * rigidbody.mass * 140
```

It does not normalize the direction, release the item, or validate state. The caller supplies the
attach point's forward direction after requesting a state-machine drop.

`Use` and `UnUse` toggle `IsUsed` and emit `ItemUsed`/`ItemUnUsed`. Repeated `UnUse` calls are
suppressed when the item is already unused.

### `ItemNonVR` bridge

During setup, `ItemNonVR` adds a `GrabHandlerItem` over the item's configured collider objects. For
usable items it sets `isHoverableWhileHeld`, `isUsable`, and continuous-use policy.

On grab it:

- unsnaps from an `ItemSnapPointBase` without forced behavior;
- fires the `ItemBase` grabbed event consumed by multiplayer;
- converts non-trigger item colliders to triggers while remembering original triggers;
- subscribes item-use events to the item's `Use`/`UnUse` implementation.

On ungrab it:

- fires the `ItemBase` ungrabbed event;
- restores originally non-trigger colliders;
- clears the remembered collider set;
- removes item-use subscriptions.

This confirms that bypassing the real grab lifecycle would miss snapping, `ItemBase` multiplayer
events, collider presentation, and use wiring.

### Grab/inventory coupling

`GrabberStashingHandler` listens to both the grabber and `Inventory.InventoryStatusChanged`.

When an item grab begins:

```text
interrupt stash animation
register held-item pose provider
if the item has InventoryItemSpec
  Inventory.EquipItem(item, hotbar.SelectedSlot, equipSlot 0)
```

When an item grab ends:

```text
interrupt stash animation
remove held-item pose provider
if the item has InventoryItemSpec
  Inventory.UnequipItem(addToInventory: false, equipSlot: 0)
```

When an inventory equip event occurs for an item that is not grabbed, it calls
`RequestForceHold(item.AGrabHandler)`. When an unequip event occurs for a grabbed item, it calls
`ItemBase.ForceEndInteraction()`.

This produces two distinct faithful paths:

- world pickup: raycast -> `RequestStartInteraction` -> grabber state machine -> grab lifecycle ->
  inventory equip side effect;
- inventory equip: `Inventory.EquipItem` -> inventory event -> `RequestForceHold` -> grabber state
  machine -> grab lifecycle.

Tests must assert both grabber state and inventory/equipped-slot projection because neither alone
proves that the complete bridge succeeded.

Inventory drop events position the object at the player's attach point or the active
`GrabHandlerItem.forcedDropAnchor`, using custom grab offsets, before physics continues.

### Desktop quick-equip and quick-move

`InventoryViewNonVR.OnSlotPressChanged` runs actions only on the press edge. It checks quick-equip
before quick-move, so quick-equip wins when both modifiers are held.

`QuickEquipAction` is the real post-input desktop boundary for:

- putting the currently held item back into inventory;
- moving a held item into an active container when no hotbar slot is available;
- equipping or swapping an active-container item;
- replacing the currently equipped item from an inventory slot.

`QuickMoveAction` validates that:

- hand slots are never quick-moved;
- a container source slot exists;
- an inventory source slot is non-empty, not dropped, and not locked.

With no active container it moves between hotbar and backpack. With an active vanilla container it
deposits only when the source is a valid hotbar index; the other branch treats the source as a
container slot and withdraws to the first free hotbar slot. This means backpack-to-container quick
move is not represented as a normal vanilla deposit branch in the captured code.

The cold-container prefix intentionally handles any non-hand, non-container inventory source as a
deposit request before vanilla mutation. Runtime tests should include hotbar and backpack cases
separately and record that the backpack behavior is a multiplayer extension rather than an exact
vanilla-container behavior.

### Inventory open/close behavior

`InventoryViewNonVR.InventoryUIOnOpenedOrClosed`:

- plays ordinary inventory sound only when no active container exists;
- requests screenspace mouse override while open;
- removes that request while closing;
- clears `ItemContainerRegistry.ActiveContainer` on close;
- emits `BigInventoryOpenChanged`.

Container test setup must therefore open through the normal access/UI path or reproduce these
preconditions explicitly before invoking inventory actions.

## Findings from the third and fourth fetches

### `ItemUseNonVR` is not the active `IItemUse` dispatcher

`ItemUseNonVR` initializes one frame after `Start`. It resolves the co-located `GrabHandlerItem` and
`ItemBase`, destroys itself if either is absent, and then follows the item's held lifecycle:

```text
ItemBase.Grabbed
  -> subscribe ItemBase.Used
  -> enable per-frame hover updates

ItemBase.Ungrabbed
  -> unsubscribe ItemBase.Used
  -> disable hover updates
```

While enabled, `Update()` copies `grabHandler.GetGrabber().Raycaster.CurrentlyHit`, chooses the hit
rigidbody GameObject before the hit collider GameObject, and passes that object to the virtual
`HandleHover(GameObject)` method. When the held item is used, the same current hit is passed to the
virtual `OnItemUsed(GameObject)` method.

This class initially looked like the non-VR item-use dispatcher, but dnSpy shows an empty `Subtypes`
section and no external users. Its virtual `HandleHover(GameObject)` and
`OnItemUsed(GameObject)` methods have no overrides in the shipped assemblies. It is therefore an
unused/stripped abstraction, not the active `IItemUse` path, and the harness must not depend on it.

`IItemUse` defines separate compatibility and action methods for hover and use:

```text
IsHoverCompatible(ItemUseTarget) -> bool
HandleHover(ItemUseTarget)       -> bool
IsUseCompatible(ItemUseTarget)   -> bool
HandleUse(ItemUseTarget)         -> bool
```

`ItemUseTarget` itself is only a marker plus a configured collider array. `ItemUseRedirect` holds a
serialized target reference and destroys itself during `Awake` if that reference is absent.

The active `IItemUse` dispatchers are `InsertItemIntoTargetHandler` and
`ItemTriggerEnterTarget`. They implement distinct raycast/click and physical trigger-entry paths.

### Job booklet and overview use handlers

`JobBookletUse` and `JobOverviewUse` are concrete `IItemUse` handlers added dynamically by their
respective booklet components. They do not inherit `ItemUseNonVR`.

Both cache the co-located booklet object in `Awake` and accept only an `ItemUseTarget` containing a
`JobValidator` component. Their hover compatibility delegates to use compatibility.

For non-VR hover they display the appropriate validator-use interaction text; hover handling
returns false in VR. Their use methods are straightforward synchronous logical calls:

```text
JobBookletUse.HandleUse(target)
  -> target.GetComponent<JobValidator>()
  -> JobValidator.ValidateJob(jobBooklet)

JobOverviewUse.HandleUse(target)
  -> target.GetComponent<JobValidator>()
  -> JobValidator.ProcessJobOverview(jobOverview)
```

The boolean means handled/compatible at this layer: missing validators return false and successful
calls return true. `HandleUse` itself does not enforce the non-VR hover restriction, so the trigger
or raycast dispatcher decides when it is invoked.

Directly calling these handlers would test job-validator logic but would bypass target acquisition,
redirects, compatibility selection, highlights, and physical insertion. End-to-end harness cases
should enter through the appropriate dispatcher once captured.

### Raycast/click target-use dispatcher

`InsertItemIntoTargetHandler` is attached to the player interaction rig and observes the real
`Grabber`. It skips target processing:

- during the same frame that an item grab begins;
- while the hotbar or large inventory is open;
- while time is paused;
- when no item is currently held;
- when the held handler's GameObject has no `IItemUse` components.

When allowed, it raycasts from `Grabber.Cursor.GetRay()` up to four metres against its configured
layer mask, includes triggers, and distance-sorts the hits. For each hit it:

1. ignores the held item and its child grab handlers when screenspace mouse is off, but aborts when
   that self-hit occurs while screenspace mouse is on;
2. resolves an `ItemUseTarget` in the collider's parents;
3. if absent, resolves an `ItemUseRedirect` in the collider's parents and uses its target;
4. permits traversal through a `StaticInteractionArea` when no target exists, but otherwise stops
   at a non-target obstruction;
5. requires the exact hit collider to appear in `ItemUseTarget.targetColliders`;
6. iterates the held GameObject's `IItemUse` components in component order.

On an `InteractionPrimary` down edge, a compatible non-animated handler receives
`HandleUse(target)`. The first handler returning true consumes the interaction. Without a consumed
use, compatible hover handlers receive `HandleHover(target)` and the first true result selects the
target's `HighlightTag`.

Highlights are reconciled every `Update`: the previous item highlight is disabled and the newly
desired highlight is enabled when the target changes.

For `IItemUseAnimated`, a compatible click stores the handler and target and starts
`ItemWorkingAnimation`; `HandleUse` is deferred until the animation's `WorkStopped` event. While
animating, the handler participates in `ItemPositionController` and derives its pose from
`IItemUseAnimated.TargetPoint`, `InteractionPoint`, and the animation's eased move-to-work progress.
The animation callbacks currently always report input pressed and work done.

This dispatcher embeds the Rewired `GetButtonDown` test inside its private per-frame method. Calling
`IItemUse.HandleUse` directly is a lower-fidelity logical test and calling `DoRaycastLogic` alone
cannot force the click branch. A future debug driver has three honest fidelity choices:

- exercise the trigger-entry path when the actual target supports it;
- use a narrowly scoped debug-only input override for the primary-button down edge, then let
  `InsertItemIntoTargetHandler.Update` run normally;
- label a direct `IItemUse.HandleUse` call as `GameplayMethodDirect`, explicitly acknowledging that
  target acquisition, redirect, collider membership, highlight, and animation dispatch were
  bypassed.

The harness must not reimplement the raycast loop as a silent substitute; that would fork game
logic and conceal compatibility changes in future DV versions.

### Physical trigger-entry target-use dispatcher

`ItemTriggerEnterTarget` supports `VR`, `NonVR`, or `All` modes. In an unsupported runtime mode it
destroys either itself or its entire GameObject according to configuration. In a supported mode it
requires an `ItemUseTarget` parent and marks itself initialized.

On trigger entry it first registers with `ReliableOnTriggerExit`, then accepts only a collider that:

- belongs to a parent `ControlImplBase`; and
- is explicitly represented by one of that control's `InteractionColliderObjects`.

If `requiresUngrab` is false, use is checked immediately. If it is true while the control is held,
the component tracks every overlapping eligible collider for that control and waits for the
control's `Ungrabbed` event. On ungrab it uses the first collider still inside the trigger.

`CheckUse` walks `IItemUse` components from the collider's parents. The first handler for which
`IsUseCompatible(target)` and `HandleUse(target)` both return true consumes the insertion. This path
does not perform hover handling or animation dispatch itself.

For physical job-validator tests, this is the preferred highest-fidelity route when the live
validator has an enabled trigger configured for the active runtime mode: move the genuinely held
booklet into the trigger, verify overlap, release through `RequestDrop`, and assert the validator
side effect. This preserves collider eligibility, optional release gating, handler compatibility,
and the real booklet method.

Observed upstream cleanup risk: when an item exits a `requiresUngrab` trigger while still held,
`OnTriggerExit` removes the last collider and dictionary entry but does not unsubscribe
`ControlImplBase.Ungrabbed`. A later ungrab sees no dictionary entry and also does not unsubscribe.
Repeated enter/exit cycles can therefore accumulate stale event subscriptions. Harness cleanup
should avoid abandoning held items inside this intermediate state, and a dedicated regression test
can determine the practical impact before we consider patching it.

### Live order-validator target configuration

A live Steel Mill order validator was inspected at:

```text
origin_shift_parent/Offices/Offices_w3/SteelMill/JobValidator
scene: game_w3
layer: Train_Interior
```

The root contains `JobValidator`, `Rigidbody`, `PlayerDistanceMultipleGameObjects`, `LODGroup`, and
`ItemUseTarget`. Its logical children include `Colliders`, `JobBookletInserted Sensor`, the active-job
booklet reprint control, lights, `MoneyPrinter`, and `BookletPrinter`.

The root `ItemUseTarget` is enabled and active and has exactly one configured target collider:

```text
path: origin_shift_parent/Offices/Offices_w3/SteelMill/JobValidator/Colliders/InsertionAreaRaycastTarget
scene: game_w3
layer: Grabbed_Item
components: Transform, BoxCollider
BoxCollider.enabled: true
BoxCollider.isTrigger: true
BoxCollider.center: (-0.1358, 0.0115, -0.0030)
BoxCollider.size: (0.1026, 0.2730, 0.5073)
attached Rigidbody: JobValidator root
```

`GetComponentsInChildren<ItemTriggerEnterTarget>(true)` returns zero. The live job validator
therefore uses `InsertItemIntoTargetHandler`'s raycast/click dispatcher rather than the physical
trigger-entry dispatcher. A gameplay-fidelity job-validator test must aim a held booklet at this
configured collider and provide a scoped primary-button down edge; trigger overlap cannot exercise
the real path on this prefab.

The live `JobValidator` component is active and enabled and references:

```text
bookletPrinter: BookletPrinter (PrinterControllerWithLamp)
moneyPrinter: MoneyPrinter (MoneyPrinterJobValidator)
reprintActiveJobBookletsButtonGO: C reprint active job booklets
jobValidatedSound: interface_alert_01
summonBookletsCoro: null while idle
```

The Steel Mill booklet printer is an active `PrinterControllerWithLamp` at:

```text
origin_shift_parent/Offices/Offices_w3/SteelMill/JobValidator/BookletPrinter
cooldown: 1.25 seconds
IsOnCooldown: false while idle
printingSound: interface_print_01
errorSound: interface_print_error_01
cooldownLamp: light_main
```

Its output transform is:

```text
path: origin_shift_parent/Offices/Offices_w3/SteelMill/JobValidator/BookletPrinter/SpawnAnchor
scene: game_w3
layer: Default
world position: (516.0396, 133.3587, 752.6724)
local position: (-0.03, 1.392, 0.165)
world Euler rotation: (34.4992, 41.5, 0)
local Euler rotation: (34.4992, 270, 0)
world forward: (0.5461, -0.5664, 0.6172)
children: 0
components: Transform only
```

Validator tests should resolve the live `spawnAnchor` reference instead of hard-coding this pose,
and should wait until `PrinterController.IsOnCooldown` is false before acting. The captured values
serve as fixture diagnostics and a sanity check that the expected machine instance was selected.

### Button and control activation

`ControlImplBase.Use()` is the shared logical-use boundary:

```text
if !InteractionAllowed: return
LastSetValueSource = Default
fire Used
```

`ButtonBase.Use()` repeats the interaction-allowed guard, calls the base method, and then performs
button-specific behavior:

- toggle buttons request and accept the opposite zero/one value and play toggle sounds;
- momentary buttons start a one-frame `1 -> 0` value coroutine, animate the press, and play sound;
- inactive momentary buttons still emit `Used` and may play sound, but do not start value or
  animation coroutines.

Event ordering is important: `Used` fires before the button changes its `Value`. Subscribers such
as job validators therefore run synchronously before the momentary click coroutine or toggle value
update.

`ButtonBase.Use()` is the recommended gameplay-method boundary for a harness action whose subject
is the button's behavior. It retains `InteractionAllowed`, `Used`, value updates, animation, and
audio while intentionally bypassing physical raycast/press mechanics. A separate physical-control
scenario should use the real `ButtonNonVR` grab handler through the ordinary grabber path.

Buttons initialize their spec-derived physics, sounds, and joint behavior one frame after enable.
The harness should wait for normal scene initialization before pressing one; it must not invoke
newly created buttons in their pre-initialized frame.

For controls, blocked interaction is enforced in two places:

- `ControlImplBase.BaseInteractionPassThrough` returns true when interaction is disallowed, allowing
  the raycaster to ignore the control;
- `ControlImplBase.Use`/`UnUse` refuse to fire events when interaction is disallowed.

This explains part of the previously unresolved interaction-permission path. It is a control-level
policy and is distinct from the `AGrabHandler.interactionAllowed` field that remains unaccounted for.

### Player teleportation

`PlayerManager.TeleportPlayer(Vector3, Quaternion, Transform, bool, bool)` is the correct shared
harness boundary. It validates that a player transform and `APlayerTeleport` singleton exist, emits
`PlayerTeleportStarted`, delegates to the active desktop/VR implementation, and then emits
`PlayerTeleportFinished`.

For desktop, `PlayerTeleportNonVR`:

1. marks the character controller as repositioning;
2. disables camera smoothing;
3. directly sets the character transform's Unity world position;
4. resolves a train interior or `CharacterReparentTarget` from the supplied target;
5. reparents the character with force enabled;
6. optionally applies look rotation and footstep audio;
7. skips one Doppler frame and clears interaction text;
8. starts a reactivation coroutine that clears repositioning immediately, updates the camera,
   waits one frame and end-of-frame, then re-enables smoothing.

`PlayerTeleportFinished` is a synchronous delegation event, not a presentation-settled event. It
fires before the reactivation coroutine has restored camera smoothing. A test driver should await
the event for call completion, then wait a frame plus end-of-frame before declaring the player
settled and entering a cross-process barrier.

The method does not release held/dragged items, close inventory/container UI, or leave a vehicle on
the harness's behalf. Test setup must explicitly normalize those states before teleporting.

The position parameter is assigned directly to `charController.transform.position`; it is the
current Unity world's shifted coordinate, not an absolute multiplayer/world-storage coordinate.
Shared test anchors expressed in absolute coordinates must be converted independently on each
process after accounting for its current origin shift.

`target` controls parenting:

- a train-related target reparents to `trainCar.interior`;
- a walkable/reparent-target object may resolve `CharacterReparentTarget.target`;
- null or unrelated targets reparent to the ordinary world.

`PlayerManager.IsPlayerPositionValid` can reject coordinates outside the configured world bounds,
but `TeleportPlayer` does not call it automatically. The harness adapter should validate before
teleporting and report an explicit setup failure for invalid anchors.

## Recommended automated entry points so far

| Action | Recommended boundary | Fidelity | Status |
| --- | --- | --- | --- |
| World pickup | Real ray acquisition followed by `GrabberInteractionHandlerDV.RequestStartInteraction()` | `GameplayMethod` | Confirmed state-machine and lifecycle ordering |
| Inventory equip | `Inventory.EquipItem(...)`, allowing `GrabberStashingHandler` to call `RequestForceHold` | `UIController`/`GameplayMethod` | Confirmed coupling |
| Normal state-machine release | `GrabberInteractionHandlerDV.RequestDrop()` | `GameplayMethod` | Confirmed synchronous holding exit |
| Throw | `RequestDrop()` then held handler `Throw(playerRig.GetAttachPoint().forward)` | `GameplayMethod` | Confirmed physical-input ordering |
| Quick equip | `InventoryViewNonVR.QuickEquipAction(...)` | `UIController` | Confirmed post-input method |
| Quick move | `InventoryViewNonVR.QuickMoveAction(...)` | `UIController` | Confirmed post-input method |
| Raycast/click item use | Real cursor aim plus a scoped primary-input edge through `InsertItemIntoTargetHandler.Update` | `PhysicalInput`/`GameplayMethod` | Dispatcher confirmed; debug input override not yet implemented |
| Physical trigger item use | Move eligible item collider into live `ItemTriggerEnterTarget`; release normally when required | `GameplayMethod` | Confirmed VR/NonVR/All dispatcher |
| Direct logical item use | Compatible `IItemUse.HandleUse(target)` | `GameplayMethodDirect` | Useful narrow test; explicitly bypasses acquisition, redirect, collider, hover, and animation |
| Logical button press | `ButtonBase.Use()` | `GameplayMethod` | Confirmed permission, event, value, animation, and audio ordering |
| Player reposition | `PlayerManager.TeleportPlayer(...)`, then wait one frame plus end-of-frame | `GameplayMethod` | Confirmed desktop reparenting and settling path |

The first debug-only adapter now implements the world pickup, normal release, throw, and player
reposition rows. Pickup calls `GrabberRaycasterDV.UpdateRaycast()` and refuses to continue unless
the resulting `CurrentlyRaycasted` handler is the requested network item. Drop calls
`RequestDrop()`. Throw retains the held handler, calls `RequestDrop()`, and then invokes
`GrabHandlerItem.Throw(...)`, matching the ordering in `GrabberInputHandler.Update`.

These adapters are intentionally NonVR-only. Missing components or a VR runtime produce
`Unsupported`; acquisition mismatch, failure to enter/leave the holding state, and a throw with no
resulting velocity are test failures. There is no force-hold or direct-transform fallback.

Tests must use guarded adapters for private methods. Failure to resolve a method or expected
component reports `Unsupported`; it must never fall back silently to `StartInteraction`, direct
parenting, or direct graph mutation.

## Initial pickup/drop/throw research targets

Capture the complete source for:

1. `DV.Interaction.Grabber`
2. `DV.Interaction.GrabberInteractionHandlerDV`
3. `DV.Interaction.GrabberInputHandler`
4. `DV.Interaction.GrabHandlerItem`
5. `DV.Interaction.AGrabHandler`
6. `DV.Interaction.GrabberRaycasterDV`
7. `DV.CabControls.NonVR.ItemNonVR`

Also capture dnSpy `Used By` results for:

- `GrabberInteractionHandlerDV.RequestStartInteraction`;
- `GrabberInteractionHandlerDV.RequestForceHold`;
- `GrabberInteractionHandlerDV.RequestDrop`;
- `Grabber.OnForceHoldRequested`;
- `Grabber.ReleaseHolding`;
- `GrabHandlerItem.StartInteraction`;
- `GrabHandlerItem.EndInteraction`;
- `GrabHandlerItem.Throw`.

## Known method inventory

The installed assemblies expose the following methods. Their behavior still requires readable
decompilation.

### `DV.Interaction.Grabber`

```text
DoUpdate
OnForceHoldRequested
ReleaseHolding
OnForceHoldingEntry
OnHoldingEntry
OnHoldingExit
OnDraggingEntry
OnDragUpdate
OnDraggingExit
EndDragForced
```

### `DV.Interaction.GrabberInteractionHandlerDV`

```text
RequestStartInteraction
RequestEndInteraction
RequestForceHold
RequestDrop
IdleStartInteraction
HoldingStartInteraction
HoldingStopInteraction
LockHolding
UnlockHolding
```

### `DV.Interaction.GrabHandlerItem`

```text
StartInteraction(Vector3, Grabber)
AttachToAttachPoint(Transform, bool)
EndInteraction
AllowPickupAndThrow
Throw(Vector3)
Use
UnUse
TogglePhysics(bool)
```

### `DV.Interaction.AGrabHandler`

```text
StartInteraction
EndInteraction
ForceEndInteraction
AllowPickupAndThrow
Throw
Use
UnUse
IsGrabbed
GetGrabber
InteractionPassThrough
```

## Questions the pickup research must answer

Resolved:

1. Normal desktop world pickup begins with a real raycast selection and
   `RequestStartInteraction`; `IdleStartInteraction` consumes `CurrentlyRaycasted`.
2. `RequestForceHold` is the inventory/hotbar equip path and bypasses world-selection validation.
3. Throw input requests drop first and then applies force along the player attach point's forward
   vector.
4. `ItemNonVR` and `GrabberStashingHandler` connect the grab lifecycle to snapping, colliders,
   `ItemBase` events, equip/unequip, and dropped-item positioning.

Still unresolved:

1. Where `interactionAllowed`, `mustHoldButton`, and `AllowPickupAndThrow` are enforced; they are
   not consumed by the captured production raycaster, interaction handler, or `Grabber`.
2. Whether every item-specific override preserves the normal base lifecycle and completion events.
3. Which of AltFuture's mock components were used together in their internal test scenes and
   whether any reusable test runner survives in the shipped assemblies.
4. Whether the stale `Ungrabbed` subscription in `ItemTriggerEnterTarget.OnTriggerExit` causes
   observable duplicate handling or meaningful long-session overhead.

## Research record template

Add one section per confirmed action using this format:

```text
### Action name

Physical input path
  Type.Method
  -> Type.Method
  -> gameplay boundary

Recommended automated entry point
  exact declaring type and signature

Fidelity
  GameplayMethod / UIController / ServerAuthority

Preconditions
  required runtime objects and state

Validation retained
  checks still exercised by this entry point

Validation bypassed
  physical-input-only checks not exercised

Observable completion
  state/event used by eventual assertions

Side effects
  inventory, physics, events, audio, animation, authority, and presentation

Cleanup requirements
  safe and idempotent teardown

Desktop/VR applicability
  NonVR / VR / shared after interaction

Game-version assumptions
  private method and field names that require guarded lookup
```

## Later research targets

After pickup/drop/throw is understood:

### Inventory

- full `InventoryViewNonVR`;
- full `InventoryViewBase`;
- full `GrabberStashingHandler`.

### Item use and buttons

- representative live component/hierarchy dump for a job-validator `ItemUseTarget` and
  `ItemTriggerEnterTarget`, including mode, `requiresUngrab`, target collider paths, and layer;
- `ItemWorkingAnimation` only when animated tool-use automation becomes an active test target;
- Rewired input interception boundary for a debug-only, one-frame primary-button edge.

### Test positioning

- origin-shift conversion used to turn a shared absolute test anchor into the current Unity world
  position on each process;
- whether teleporting out of a train requires an explicit `PlayerManager.SetCar(null)` or whether
  character reparenting updates it elsewhere;
- the VR `APlayerTeleport` implementation only when VR automation becomes an active goal.

Fast-travel UI/controller behavior is not required for test arrangement. It should be researched
separately when fast travel itself becomes the behavior under test.

## Intended output

Research in this file feeds one maintainable, debug-only gameplay-driver adapter. Tests must not
contain scattered reflection or private Derail Valley method names. If an adapter cannot resolve a
required entry point for the running game build, dependent tests report `Unsupported` rather than
falling back to a lower-fidelity direct mutation.

## Live job-validator hierarchy (UnityExplorer, 2026-07-16)

The Steel Mill validator confirms that item-use tests can resolve the live machine structurally:

```text
origin_shift_parent/Offices/Offices_w3/SteelMill/JobValidator
  JobValidator
  Rigidbody
  PlayerDistanceMultipleGameObjects
  LODGroup
  ItemUseTarget
  Colliders/InsertionAreaRaycastTarget
    BoxCollider (trigger, layer Grabbed_Item)
  BookletPrinter
    PrinterControllerWithLamp
    SpawnAnchor
```

Observed values:

- `ItemUseTarget.targetColliders` has one entry: `InsertionAreaRaycastTarget`;
- the target collider is an enabled trigger on the `Grabbed_Item` layer;
- collider local center is approximately `(-0.1358, 0.0115, -0.003)`;
- collider size is approximately `(0.1026, 0.273, 0.5073)`;
- `JobValidator.bookletPrinter` resolves to the `BookletPrinter` child;
- `PrinterController.spawnAnchor` resolves to its `SpawnAnchor` child;
- at the observed Steel Mill origin, the spawn anchor absolute position was approximately
  `(516.0396, 133.3587, 752.6724)` before accounting for later origin shifts.

The absolute coordinates are diagnostic only. Automated tests should find the nearest active
`JobValidator`, follow its component references, and convert through `WorldMover.currentMove`.
The runtime-test capability endpoint now reports this structural anchor when present.
