# Derail Valley Career Manager UI Extension Logic

> Status: current-game reverse-engineering and proposed internal multiplayer API. No API or Lost Items screen described here is implemented yet unless explicitly noted.

## Goal

Create a small internal Career Manager extension API that can host Lost Items now and other multiplayer features later without replacing Derail Valley's input routing or rebuilding the entire vanilla menu.

The API should provide:

```text
one stable MULTIPLAYER entry on every Career Manager main screen
one mod-owned paginated extension hub
registered feature screens created per terminal
native controller, keyboard, and VR input through DisplayScreenSwitcher
clean availability, error, loading, empty, and confirmation states
no unconditional Harmony input override
```

Related documents:

- `DERAIL_VALLEY_LOST_AND_FOUND_LOGIC.md` consumes this API for Lost Items.
- `DERAIL_VALLEY_ITEM_STORAGE_LOGIC.md` documents the underlying inventory/storage behavior.

## Confirmed native contracts

### `IDisplayScreen`

Every screen implements:

```csharp
void Activate(IDisplayScreen previousScreen);
void Disable();
void HandleInputAction(InputAction input);
```

`DisplayScreenSwitcher` accepts any live `IDisplayScreen`; there is no central vanilla registration table.

`DisplayScreen` is only an abstract `MonoBehaviour` implementation of those same three methods. It adds no hidden lifecycle or state. Mod screens can safely inherit `DisplayScreen` for consistency or implement `IDisplayScreen` directly.

### `InputAction`

```text
None
Up
Down
Cancel
Confirm
PrintInfo
```

The Career Manager's physical controls, non-VR input, and VR controls are already normalized into this enum before reaching a screen. Custom screens should consume these actions instead of reading keyboard or mouse input directly.

### `CareerManagerInputHandler` and VR

`CareerManagerInputHandler` wires the five physical `ButtonBase.Used` events to `DisplayScreenSwitcher.HandleInput()`:

```text
upButton       -> Up
downButton     -> Down
cancelButton   -> Cancel
confirmButton  -> Confirm
printInfoButton -> PrintInfo
```

It activates `nonVrMouseInput` only when VR is disabled. In VR, the same physical buttons still raise `ButtonBase.Used`, so the normalized `InputAction` route is shared. A custom `IDisplayScreen` therefore receives VR interaction automatically and needs no headset/controller polling or VR-specific patch.

The input handler also applies `GameFeatureFlags.Flag.UseCareerManager` before forwarding. The internal API should preserve that gate by leaving the handler untouched.

### `DisplayScreenSwitcher`

`SetActiveDisplay(nextScreen)`:

1. disables the current screen;
2. starts a player-distance reset coroutine for non-idle screens;
3. assigns `CurrentScreen`;
4. calls `nextScreen.Activate(previousScreen)`;
5. raises `DisplayScreenUpdated`.

`HandleInput(input)`:

1. rejects globally blocked actions;
2. plays the input sound;
3. forwards the action to `CurrentScreen.HandleInputAction(input)`.

This is already the correct input router. Do not patch or replace it.

The switcher also resets to its idle screen when the player leaves. A custom screen automatically receives the same lifecycle if it is activated through `SetActiveDisplay()`.

### `CareerManagerMainScreen`

The main screen is hard-coded around four choices:

```text
0 Fees
1 Licenses
2 Owned Vehicles
3 Stats
```

Its `Awake()` creates a four-element `selectableText` array, sets `activeSlotCount = 4`, and constructs a four-element `IntIterator`. Its `Activate()`, `Disable()`, `HandleInputAction()`, and `GetCurrentSelection()` all explicitly know those four entries. Confirm is a switch over indices 0-3.

Although it inherits `ScrollableDisplayScreen`, it is not a general virtualized menu. It does not populate a changing window of entries from `IndexOfFirstDisplayedEntry`. Appending many conceptual entries while keeping four visual rows would break selection and confirmation.

### `ScrollableDisplayScreen`

The native scrolling base provides:

- visible-row selection through `IntIterator`;
- `IndexOfFirstDisplayedEntry`;
- bounds repair when data count changes;
- wrapping;
- scroll arrows;
- `PopulateTextsFromIndex()` and `HighlightSelected()` hooks.

For a dynamic screen, the selected model index is:

```csharp
int absoluteIndex = IndexOfFirstDisplayedEntry + selector.Current;
```

`IntIterator` behavior is now confirmed:

- `UpdateLength()` preserves `Current` and clamps it only when the new length is positive;
- a zero-length iterator may retain `Current == 0`, so callers must check `HasElements` before indexing;
- `Next()` and `Previous()` return `-1` when empty;
- wrapping is fixed at construction;
- changing `Current` raises `CurrentUpdated` only when the numeric value changes;
- replacing the iterator would discard subscribers, so the extension must call `UpdateLength(5)` on the existing main-screen iterator.

### Native screen patterns

`CareerManagerFeesScreen` is the useful dynamic-list reference:

- constructs a visible-row selector;
- refreshes the model on activation;
- preserves or resets selection based on the previous screen;
- repairs selector and first-index bounds after data changes;
- populates a fixed row pool;
- subscribes only while active;
- supports Up, Down, Cancel, Confirm, and PrintInfo;
- clears text and stops refresh work on disable.

`CareerManagerStatsScreen` is the useful static-detail reference: populate fixed fields on activate, clear them on disable, and return to main on Cancel/Confirm.

## Why the historical external API is not the implementation

The supplied historical Career Manager API proves that TMP rows and `IDisplayScreen` instances can be added at runtime. Its patching strategy is too invasive:

- it always returns `false` from a prefix on `CareerManagerMainScreen.HandleInputAction()`;
- it reimplements all main input and leaves Cancel as a no-op;
- it wipes and rebuilds vanilla `selectableText`;
- it hard-codes the current four vanilla fields and hierarchy;
- it identifies duplicate entries by display text rather than stable ID;
- its before/after ordering can fail when anchors are missing;
- it creates bare GameObjects for screens without guaranteeing their required references.

The multiplayer repository does not currently reference that API. Do not add a second unconditional input override. If a future decision makes a current external API a dependency, verify its exact installed version and integrate through a stable public registration surface instead of competing Harmony prefixes.

## Chosen architecture

Add exactly one physical row to the vanilla main screen:

```text
0 Fees
1 Licenses
2 Owned Vehicles
3 Stats
4 Multiplayer
```

Selecting Multiplayer opens a mod-owned paginated extension hub:

```text
Multiplayer
  Lost Items
  future: Session Information
  future: Player/Permissions
  future: Server Settings
  future: Diagnostics
```

This confines the brittle vanilla integration to one known fifth row. All future extensibility lives in code we control.

## Internal API model

Suggested files:

```text
Multiplayer/Components/UI/CareerManager/
  CareerManagerExtensionEntry.cs
  CareerManagerExtensionRegistry.cs
  CareerManagerTerminalContext.cs
  CareerManagerExtensionHost.cs
  CareerManagerExtensionHubScreen.cs
  CareerManagerListScreen.cs
  CareerManagerScreenFactory.cs

Multiplayer/Patches/UI/
  CareerManagerMainScreenExtensionPatch.cs
```

### Registration contract

```csharp
internal sealed class CareerManagerExtensionEntry
{
    public string Id { get; init; }
    public Func<CareerManagerTerminalContext, string> Label { get; init; }
    public int Order { get; init; }
    public Func<CareerManagerTerminalContext, CareerManagerAvailability> CanOpen { get; init; }
    public Func<CareerManagerTerminalContext, IDisplayScreen> CreateScreen { get; init; }
}

internal readonly struct CareerManagerAvailability
{
    public bool Available { get; init; }
    public string Reason { get; init; }
}
```

Registration should:

- require a stable non-empty ID;
- reject duplicate IDs;
- order by numeric order and stable ID;
- return `IDisposable` so UMM unload can unregister cleanly;
- store definitions only, never Unity objects or terminal-specific screens;
- snapshot definitions when a hub activates.

Avoid before/after string anchors. Stable ordering is simpler and cannot cycle.

### Per-terminal context

```csharp
internal sealed class CareerManagerTerminalContext
{
    public CareerManagerMainScreen MainScreen { get; init; }
    public DisplayScreenSwitcher ScreenSwitcher { get; init; }
    public Transform ScreenRoot { get; init; }

    public string LocationId { get; init; }
    public string LocationName { get; init; }
    public StationController Station { get; init; }
    public TrainCar TrainCar { get; init; }

    public byte LocalPlayerId { get; init; }
    public bool IsHost { get; init; }
}
```

Every physical Career Manager gets its own context, host component, hub, and feature screen instances. Never store one global `CareerManagerMainScreen` or share a screen MonoBehaviour between terminals.

## Minimal Harmony boundary

### `CareerManagerMainScreen.Awake` postfix

After vanilla constructs its four rows:

1. attach one `CareerManagerExtensionHost` if absent;
2. clone only the Stats TMP row for the Multiplayer entry;
3. position it using the Owned Vehicles-to-Stats row delta;
4. strip unrelated functional components if the cloned object contains any;
5. append it to the private `selectableText` array;
6. add it to `screenSwitcher.allTextFields` and immediately clear its text;
7. update `activeSlotCount` to five;
8. call the existing selector's `UpdateLength(5)` rather than replacing the selector;
9. create the per-terminal hub and registered feature screens.

Cache Harmony `FieldRef` accessors once. Do not use reflection during input or rendering.

Before implementing, verify on a live Career Manager hierarchy that a fifth line fits. If it does not, adjust the layout once or replace the top-level presentation with another deliberate design; do not pretend the native main screen can virtualize arbitrary entries.

This hierarchy check does not require more dnSpy work from the user. At implementation time, the Awake postfix can emit a one-shot debug dump containing the main-screen GameObject path, each TMP child path, local position, font size, bounds, and parent. The fifth row can then be cloned and positioned from the measured `Owned Vehicles -> Stats` delta. If necessary, the observability component-hierarchy dumper can be generalized from selected items to an arbitrary GameObject.

### `Activate` postfix

- set the Multiplayer label after vanilla writes its four labels;
- refresh availability indicators;
- ensure selector length remains five;
- do not recreate rows or screens.

### `Disable` postfix

- clear the Multiplayer TMP field;
- allow vanilla to clear and unhighlight its own fields;
- do not destroy the hub, because the terminal reuses it.

### `HandleInputAction` prefix

Return `true` for:

- every action except Confirm;
- Confirm on indices 0-3.

Only when Confirm targets index 4:

1. evaluate whether Multiplayer services are available;
2. open the hub or native-compatible info screen;
3. return `false` to prevent vanilla's `default: Unhandled case` branch.

Do not intercept Up, Down, Cancel, PrintInfo, or native confirmations.

### Optional `GetCurrentSelection` postfix

Return the Multiplayer label for index 4 if telemetry, accessibility, or tutorial code asks for the current selection. Otherwise this patch is not required for navigation.

No `HighlightSelected` patch is necessary when the private array really contains the fifth TMP row.

## Extension hub behavior

`CareerManagerExtensionHubScreen` should implement `IDisplayScreen` and own a fixed pool of visible TMP rows.

On activate:

1. snapshot registered extension definitions;
2. evaluate labels and availability using the terminal context;
3. repair selection bounds;
4. populate visible rows and arrows;
5. subscribe only to events needed while visible.

Input:

```text
Up        -> previous row / scroll
Down      -> next row / scroll
Cancel    -> switcher.SetActiveDisplay(mainScreen)
Confirm   -> open available feature; otherwise show reason
PrintInfo -> optional feature description
```

On disable, clear every owned TMP field and unsubscribe active events. Do not destroy feature screens.

Runtime registry changes should normally appear on the next hub activation. If hot registration is later required, defer rebuilding until the hub is idle or preserve selection by stable ID.

## Reusable list-screen base

A mod-owned `CareerManagerListScreen<T>` can capture the useful behavior of `CareerManagerFeesScreen` without depending on its private `FeeEntry` type.

It should provide:

- fixed visible row pool;
- absolute selection calculation;
- selection preservation by stable model key;
- scroll arrows;
- Loading, Empty, Error, and Ready states;
- primary Confirm and optional PrintInfo actions;
- pending-operation presentation;
- refresh/event subscription only while active;
- explicit previous/return screen;
- no per-frame reflection or hierarchy scans.

This base is useful for Lost Items, player lists, permissions, server settings, and other future Career Manager pages.

## License presentation

Do not add a Multiplayer hub entry for licenses and do not represent license papers in Lost and Found. Derail Valley's existing `CareerManagerLicensesScreen` is already the correct list UI.

The multiplayer implementation treats acquired licenses as shared host-career state:

- purchases made by a client are requested from and validated by the host;
- the host mutates its authoritative `LicenseManager`;
- acquisition events are broadcast to all ready clients;
- joining clients load the host save's license data;
- `NetworkClient.OnClientboundLicenseAcquiredPacket()` refreshes all live `CareerManagerLicensesScreen` instances after updating the local mirror.

Consequently, the vanilla Licenses row should remain unchanged and display the licenses available to the current multiplayer career. Physical license creation, storage, ownership, and retrieval are separate legacy presentation side effects and are excluded from this UI API.

## Lost Items integration

Lost and Found registers one hub entry:

```csharp
CareerManagerExtensionRegistry.Register(new CareerManagerExtensionEntry
{
    Id = "multiplayer.lost-items",
    Order = 100,
    Label = _ => "LOST ITEMS",
    CanOpen = context => LostItemsAvailability.For(context),
    CreateScreen = context => LostItemsCareerManagerScreen.Create(context)
});
```

The generic Career Manager API must not know about Lost and Found packets, registries, or item state.

The Lost Items screen:

- requests or snapshots only the authenticated local player's host-authoritative list;
- never uses vanilla shed membership as its UI data model;
- shows Loading, Empty, Error, and Ready states;
- preserves selection by NetId/revision when refreshed;
- uses Confirm to request retrieval;
- uses PrintInfo for reason, owner, time, and debug identity details;
- keeps Cancel available while a request is pending;
- disables only duplicate Confirm/PrintInfo operations during that pending request;
- refreshes only after an authoritative response or registry delta;
- never directly mutates inventory or the host registry.

## Compatibility and lifecycle

- Register extension definitions during multiplayer mod initialization before terminal instances normally awaken.
- If a terminal already exists, attach its host safely and materialize the one bridge row once.
- Preserve existing `CareerManagerFeesScreenPatch`, license purchasing, license replication, and the vanilla Licenses screen; this API does not replace those paths.
- Treat every terminal as independent, including station and train/caboose Career Managers.
- Add created TMP objects to `screenSwitcher.allTextFields`, but clear them immediately because Awake ordering may vary.
- Destroy host-created GameObjects and unregister definitions on UMM unload.
- Feature screens must unsubscribe network and game events in `Disable()` and `OnDestroy()`.
- Availability is evaluated on activation and confirm, not frozen at Awake.
- Never identify registrations by localized label.
- Never rebuild the menu every frame.

## Verification

Pure tests should cover:

```text
duplicate registration ID rejected
stable ordering by order and ID
registration disposal removes definition
availability failure preserves navigation and exposes reason
hub selection preserved by stable ID after refresh
empty registry produces empty state
list selection bounds after removals
pending operation rejects duplicate confirm but allows cancel
separate terminal contexts never share screen instances
```

In-game acceptance:

1. Every Career Manager shows exactly one Multiplayer row.
2. Vanilla Fees, Licenses, Owned Vehicles, and Stats behave unchanged.
3. Up/Down highlighting includes the fifth row.
4. Confirm on the fifth row opens the hub without an unhandled-index log.
5. Cancel returns from a feature to the hub and from the hub to main.
6. Moving away resets through the native switcher lifecycle.
7. Non-VR, controller, and VR controls all work without a mouse.
8. Two different Career Manager terminals hold independent screen instances and selection state.
9. UMM unload removes created objects and event subscriptions cleanly.

## Remaining dnSpy/runtime inspection

The screen base, scrolling interface, iterator, and physical/VR input route are now confirmed. Remaining inspection can be performed during implementation:

- one-shot runtime Career Manager GameObject/TMP hierarchy and spacing dump;
- `startScreenGO`/idle screen identity, visible directly from each live switcher;
- whether train/caboose Career Managers use the same layout, detected per terminal by the host component.

These are implementation details, not unresolved architectural decisions.

## Implemented WIP (2026-07-14)

An internal extension layer is now attached independently to every live `CareerManagerMainScreen`:

- a fifth `MULTIPLAYER` row is cloned from the native TMP layout and inserted into the main selector;
- Confirm opens a multiplayer hub using the existing `DisplayScreenSwitcher` and physical/VR `InputAction` route;
- the initial `LOST ITEMS` screen requests only the authenticated client's host-authoritative list;
- Up/Down navigate without a mouse, Confirm requests retrieval, Print Info shows NetId/reason, and Cancel returns through the hub;
- selection is preserved by NetId across authoritative refreshes, long lists window through the available native rows, and duplicate retrieval requests are gated;
- the vanilla Licenses page remains the entitlement display. No replacement license screen or physical paper model was added.

The current hub registry is intentionally internal and small. It establishes a reusable boundary for later multiplayer Career Manager pages without taking ownership of packet or feature-specific logic.

Runtime verification is still required for fifth-row spacing on every station/caboose layout, highlight colours, controller and VR interaction, terminal reactivation, two terminals loaded together, and teardown. If a particular terminal cannot fit the cloned fifth row, adjust that terminal's cloned layout rather than replacing the native input/switcher flow.
