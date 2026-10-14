# Derail Valley PageBook Logic and Multiplayer Notes

## Scope

This note records the page/book behavior observed in the decompiled `Page`, `PageBook`, `DV.Items.JunctionMap`, `JobBooklet`, `JobOverview`, and rendered-booklet call graphs. It identifies the smallest useful multiplayer state, the correct application API, lifecycle hazards, and the remaining decompilation targets.

The primary conclusion is:

> Synchronize `PageBook.currentPage`, not individual page renderers, animator time, page transforms, or materials.

`PageBook` already owns the semantic page state and exposes the lifecycle and application methods needed for synchronization.

## Class responsibilities

### `Page`

`Page` controls one physical animated sheet. It owns:

- the `SkinnedMeshRenderer` for that sheet;
- the page-flip `Animator`;
- the starting and ending vertical offsets;
- the temporary flip direction;
- the coroutine that updates the sheet position during animation.

Important methods:

- `Flip(float speedMult)` starts or reverses the page animation.
- `IsFlipping()` reports whether its animation is still between endpoints.
- `ForceEndAnimation()` moves an interrupted animation to its correct endpoint.

This is presentation state. Animator normalized time, `flippingDirection`, offsets, and the renderer's temporary animation settings should not be networked. Each process can animate locally from the shared page index.

`Page.OnDisable()` stops an active animation coroutine. `PageBook.OnDisable()` subsequently calls `ForceEndAnimation()` for every page, ensuring that disabling or inventory transitions leave the book in a stable visual state.

### `PageBook`

`PageBook` is the semantic page state machine.

Relevant state:

- `currentPage`: the current zero-based page index;
- `PageNum`: the number of entries in `pageTextures`;
- `PagesGenerated`: whether the runtime page hierarchy exists;
- `pages`: runtime `Page` components;
- `motionPivot`: the generated page-stack pivot.

Relevant events:

- `PageFlipped(int)`: raised after `currentPage` changes;
- `PageBookGenerated`: raised after all runtime pages, colliders, render caches, highlight geometry, and optional book-volume geometry are ready.

Relevant mutation methods:

- `FlipBy(int)`: relative local input operation;
- `FlipTo(int)`: clamps the target, animates all intervening pages, plays audio, updates `currentPage`, and raises `PageFlipped`;
- `ForceCurrentPage(int)`: clamps the target, rapidly moves all intervening pages to their endpoints, updates `currentPage`, and raises `PageFlipped` without normal flip audio;
- `ForceCurrentPageOnInitialization(int)`: waits until `PagesGenerated`, then calls `ForceCurrentPage`.

`ForceCurrentPage(int)` is the best existing API for applying remote state. It preserves the complete page-stack behavior and raises the same semantic event used by consumers, without replaying local input or ordinary page-turn audio.

### `JunctionMap`

`JunctionMap` treats `PageBook.currentPage` as gameplay state, not merely visual state.

During startup it:

1. builds one junction-coordinate map per page;
2. reads the initial `PageBook.currentPage`;
3. calls `OnPageFlipped(currentPage)`;
4. subscribes to `PageBook.PageFlipped`.

`OnPageFlipped(int)` updates both its local `currentPage` and `touchscreen.validSections`. Therefore, applying a remote page by directly moving renderers would leave the visible map page and interactive junction grid inconsistent. Applying it through `PageBook.ForceCurrentPage()` correctly raises `PageFlipped` and refreshes junction interaction.

### `JobBooklet`

`JobBooklet` does not itself control page selection or page rendering. It owns:

- the assigned `Job` reference;
- save/load of the job ID;
- job completion/abandonment listeners;
- essential-item and respawn behavior;
- booklet destruction.

Its page behavior should therefore be synchronized through the `PageBook` component on the same GameObject. Job identity/content construction and current page are separate concerns:

- job synchronization determines what content the locally created booklet contains;
- `PageBook.currentPage` determines which physical page is currently exposed.

### `JobOverview`

`JobOverview` is intentionally small and does not contain hidden page-state logic. On `Start()` it verifies that its `Job` reference has been assigned and adds `JobOverviewUse`. The remaining methods localize the item name and safely destroy the overview, including ending an active grab first.

This means the overview's rendered contents and physical page behavior are assembled by other components on the same object. The useful multiplayer boundaries remain:

- the synchronized job identity/content input;
- the generated `PageBook` content lifecycle;
- the current `PageBook.currentPage` value.

There is no separate page number in `JobOverview` that also needs synchronization.

### Rendered booklet generation

The `PageBook.Generate()` usage graph establishes that generation is not always a simple synchronous `Start()` operation:

- `RenderedTexturesBooklet.OnBookletTexturesGenerated(...)` assigns its generated texture set to the `PageBook`, then calls `Generate()`;
- `MultipleRenderedTexturesBooklet.OnBookletTexturesGenerated(...)` waits for and combines multiple rendered texture sets, then calls `Generate()`;
- `PageBook.Start()` is also a possible generation entry point for booklets whose textures are already available.

This matters for job documents. A network snapshot can arrive after the Unity object exists but before the render-texture pipeline has supplied all pages. `PagesGenerated == false` is therefore a normal readiness state, not evidence that the item is broken or missing.

Do not infer page readiness from `JobOverview.Start()`, `NetworkedItem.Awake()`, or the presence of a `PageBook` component alone. `PageBookGenerated` is the reliable visual readiness boundary.

The concrete implementations confirm the following lifecycle.

#### `RenderedTexturesBooklet`

Its texture callback:

1. invokes `RenderedTexturesBase.OnBookletTexturesGenerated(...)`;
2. assigns the received `Texture[]` directly to `pageBook.pageTextures`;
3. calls `pageBook.Generate()`.

There is no intermediate page-state restoration in this class. Any pending network page must therefore be applied by the page adapter when `PageBook` reports that generation has completed.

#### `RuntimeRenderedStaticTextureBooklet`

The schematic's rendered-booklet component derives from `RenderedTexturesBooklet`. In `Awake()` it:

1. optionally resolves a world-specific prefab through `LevelInfo.GetWorldSpecificPrefab(...)`;
2. uses that prefab's name as `renderPrefabName`;
3. destroys only the rendered-booklet component if no render prefab can be resolved;
4. otherwise calls `BookletCreator_StaticRenderBooklet.Render(gameObject, renderPrefabName)`.

The inherited `RenderedTexturesBooklet` callback then receives the locally generated static textures, assigns them to `PageBook.pageTextures`, and calls `Generate()`.

This confirms that schematic content is local, world-dependent derived state. Multiplayer should synchronize the semantic page index after both processes have generated their appropriate local world schematic. A useful discontinuity is therefore not texture identity but:

```text
renderPrefabName
worldSpecific/worldSpecificPrefab
PageNum
pages.Count
currentPage
```

If the local render component destroys itself because its prefab cannot be resolved, incoming page state should remain bounded/deferred or be reported as locally unrenderable; it must not repeatedly attempt application forever.

#### `BookletCreator_StaticRenderBooklet`

The static creator completes the content pipeline. `Create(...)` is only a convenience wrapper around `Resources.Load(prefabName)` and `Object.Instantiate(...)`.

`Render(existingBooklet, renderPrefabName)`:

1. loads and instantiates the named render prefab at `RenderTextureSystem.Instance.transform.position`;
2. retrieves `StaticTextureRenderBase` from that separate producer object;
3. retrieves the existing item's `RenderedTexturesBase` component;
4. registers the producer with `RenderedTexturesBase.RegisterTexturesGeneratedEvent(...)`;
5. starts `GenerateStaticPagesTextures()`;
6. immediately returns the existing booklet component.

The render producer is therefore a temporary, separate Unity object. It is not part of the network item's hierarchy and should not be expected in a component-tree capture of the schematic. The network item becomes visually ready later through the already documented `TexturesGenerated -> pageTextures -> PageBook.Generate()` callback.

No network synchronization should wait for, identify, or retain the temporary producer object. Readiness belongs to the resulting `PageBook.PagesGenerated` state.

#### `MultipleRenderedTexturesBooklet`

This variant supports multiple independent `BookletTextureRender` producers. Registration preserves producer order by appending a renderer and a parallel null texture slot. Each completion callback:

1. finds the sender's registered index;
2. unsubscribes that sender;
3. stores its texture array in the matching slot;
4. increments `renderersProcessed`;
5. waits until every registered renderer has completed;
6. flattens the arrays in registration order with `SelectMany`;
7. assigns the flattened array to `pageBook.pageTextures`;
8. calls `pageBook.Generate()`;
9. sets its own `pageBookGenerated` flag;
10. clears its temporary renderer and texture lists.

If a callback comes from an unregistered renderer, it logs an error and destroys the entire booklet GameObject. On destruction it unsubscribes unfinished renderers, requests their render cancellation, and destroys both collected textures and generated page textures.

This gives the network adapter two important rules:

- never retain references to generated textures in debug/network snapshots;
- tolerate the item disappearing while a page application is pending, because render cancellation/destruction is a supported lifecycle outcome.

There is also a subtle ordering detail: `MultipleRenderedTexturesBooklet.pageBookGenerated` is set only after `pageBook.Generate()` returns. If `PageBookGenerated` is raised synchronously inside `Generate()`, a listener handling that event can observe `PageBook.PagesGenerated == true` while `MultipleRenderedTexturesBooklet.IsPageBookGenerated() == false`. The page adapter should use `PageBook.PagesGenerated` as its readiness source and must not require both flags during that callback.

### `BookletCreator_JobOverview`

The creator confirms that visible job-overview content is locally generated from synchronized job data rather than serialized as textures.

For `Create(Job_data, ...)` it:

1. loads and instantiates the `Resources/JobOverview` prefab at the requested world transform;
2. names it `JobOverview[{job.ID}]`;
3. separately instantiates `Resources/JobOverviewRender` at the `RenderTextureSystem` position;
4. obtains the overview prefab's existing `RenderedTexturesBase` component;
5. registers the render producer's `TexturesGenerated` callback with that component;
6. converts the `Job_data` into `TemplatePaperData`;
7. starts asynchronous texture generation;
8. immediately returns the existing rendered-booklet component.

The `Create(Job, ...)` overload calls that path first, then adds `JobOverview` to the resulting GameObject and assigns its live `Job` reference.

For the supported haul and shunting overview types, the supplied creator methods each return a list containing one `FrontPageTemplatePaperData`. Therefore job overview content is fundamentally a single generated paper in these paths; multi-page behaviour is more relevant to job booklets, maps, manuals, and other `PageBook` items.

The multiplayer implication is that host authority should synchronize the job identity and job data needed to reproduce the page. Each process should run the normal creator/render pipeline locally. Texture objects, materials, and renderer state must not be copied across the network.

### Live `MapSchematic` composition

A component-hierarchy capture of a dropped, open `MapSchematic` confirms that it is a prefab assembled from reusable behaviours rather than a dedicated `MapSchematic` class.

The captured root contained:

```text
PageBook
ChapterSelector
DV.Booklets.Rendered.RuntimeRenderedStaticTextureBooklet
DV.Items.JunctionMap
ItemScrollingNonVR
DV.Interaction.GrabHandlerItem
NetworkedItem
the ordinary item, storage, snapping, physics and respawn components
```

At capture time its semantic state was:

```text
currentPage: 4
PageNum: 61
PagesGenerated: true
pages.Count: 61
pageTextures.Length: 61
NetworkedItem state: Dropped
IsGrabbed: false
Rigidbody.isKinematic: false
```

This is direct evidence that page selection is ordinary `PageBook` state even for the schematic. The map-specific `JunctionMap` behaviour consumes that state through `PageFlipped`; it does not replace it.

The generated hierarchy contained exactly 61 runtime `Page` objects under `Pivot`, matching both `PageNum` and `pageTextures.Length`. Each page owned an `Animator` and a child `SkinnedMeshRenderer`. At rest all page animators were disabled with `AlwaysAnimate` culling, which matches `Page.LerpOffsetCo()` disabling the animator after a flip completes.

The open-page layout also exposes the meaning of `currentPage = 4`:

- pages 0 through 3 were settled on the flipped stack;
- page 4 was the first page on the current/unflipped side;
- pages 5 through 60 remained progressively offset beneath it.

This reinforces that only the integer boundary needs replication. Sending individual sheet transforms would duplicate deterministic `PageBook` layout logic and would be vulnerable to small animation-time differences.

The prefab also contained two touchscreen assemblies:

- `TouchscreenPagesAnchor/Touchscreen`;
- `TouchScreenMapAnchor/Touchscreen`.

`JunctionMap.OnPageFlipped()` must therefore run after a remote page application so these controls receive the correct valid map sections.

Finally, the live hierarchy contained `PageFlippingHelperMapSchematic(Clone)` with active `Previous` and `Next` button colliders while the map was dropped and not grabbed. Together with the active root `ItemScrollingNonVR`, this proves that world/table interaction is an intended input path. Network validation must not require the sender to hold the map.

### Page input paths

The input implementations distinguish how a user requests a page change, but they all converge on the same `PageBook` semantic state.

#### Physical previous/next controls

`PageFlippingHelper` resolves a parent `PageBook`, resolves two `ButtonBase` controls, and subscribes to their `Used` events. The callbacks are exactly:

```text
Previous.Used -> PageBook.FlipBy(-1)
Next.Used     -> PageBook.FlipBy(+1)
```

It performs no grabbed/held check. This is the definitive dropped-book and table-top interaction path. The helper tears down its button subscriptions on destruction.

#### Desktop scrolling

`ItemScrollingNonVR` initializes at the end of the first frame, resolves `GrabHandlerItem`, and subscribes to mouse-wheel input only between `Grabbed` and `UnGrabbed`. It marks `ItemNonVR.isHoverableWhileHeld = true` and suppresses scrolling while the hotbar is open or while screenspace mouse targeting is inconsistent with the grabbed item.

Therefore desktop wheel scrolling is a held-item convenience path. It does not contradict dropped interaction because the physical previous/next button controls remain a separate path.

#### VR controller scrolling

`ItemScrollingVR` requires `VRTK_InteractableObject_DV`, marks the item scrollable, and attaches controller input only while grabbed. Valid scrolling requires the controller's modified-use input. It supports directional inversion and optional left-hand mirroring.

A held direction starts continuous scrolling:

```text
initial repeat delay: 0.4 seconds
first repeats:        every 0.25 seconds
after five repeats:   every 0.1 seconds
```

This can produce ten semantic page changes per second. Do not send a bespoke packet directly from every VR input callback. Let the ordinary tracked-value/item update loop observe the latest `currentPage`, coalesce naturally within its tick cadence, and send the canonical value. Observability may record each local input separately, but packet creation should remain bounded.

Physical VR interaction with a dropped book still comes through `PageFlippingHelper`, not `ItemScrollingVR`. Authority must cover both held-controller and world-button inputs.

#### Mixed-layer invariant and multiplayer fix

The dropped schematic intentionally uses a mixed-layer hierarchy:

```text
MapSchematic root                         -> World_Item
generated Page/Paper objects              -> World_Item
TouchscreenPagesAnchor/Touchscreen        -> Inventory
TouchScreenMapAnchor/Touchscreen           -> Inventory
PageFlippingHelper/Previous and Next       -> Inventory
BookVolumeModel and highlight helpers      -> Default
```

Those child layers are not stale inventory presentation. They remain present while the root is dropped, active, non-kinematic, and correctly parented in the world. They are part of the item's interaction/render setup.

The previous multiplayer `NetworkedItem.SetWorldPresentation()` recursively assigned `World_Item` to every descendant. That conflicted with this known-good live hierarchy and could break touchscreen, page-button, highlighting, or VR raycast behavior after a remote `Dropped`/`Thrown` snapshot even when the root item otherwise appeared correct.

This has now been fixed by assigning `World_Item` only to the root item. Existing item-specific child layers are preserved. The observability event `item.world-presentation-layer-applied` records the root transition and descendant layer histograms before and after application so a regression is visible without dumping the full hierarchy.

A useful in-game acceptance comparison remains:

1. capture a locally dropped map hierarchy;
2. capture the same map after a remote world-state application;
3. compare root and interaction-control layers;
4. verify both players can use previous/next and map touchscreen controls while it remains dropped.

## Existing save behavior

`PageBook` already establishes `currentPage` as persistent semantic state:

```text
save key: BookletPageNumber
default: 0, omitted from JSON
non-default: saved as an integer
```

On load, `ForceCurrentPageOnInitialization()` waits for page generation and then applies the saved page. Multiplayer should mirror this readiness behavior.

This also means the network page field should be an integer with the same clamp semantics as `ForceCurrentPage()`.

## Implemented multiplayer state

Register one tracked item value:

```text
key: pageBook.currentPage
type: int
getter: PageBook.currentPage
setter: apply through PageBook.ForceCurrentPage(value)
authority: host-ordered, interaction-writable by any participant
```

This is implemented by `NetworkedPageBookState`. Registration happens from the ordinary `NetworkedItem.Register()` lifecycle, with an observational `PageBook.Start()` patch as a fallback for late/dynamic component attachment. Local `PageFlipped` events flow through the normal tracked-value/ObjectState path; no separate page packet or replacement input path was introduced.

Do not synchronize:

- `Page.AnimationNormalizedTime`;
- `flippingDirection`;
- per-page local positions;
- animator enabled state;
- renderer enabled state;
- materials or generated material instances;
- `motionPivot` offsets;
- `bookVolumeModel` active state.

Those values are derived presentation state and may legitimately differ during animation or lifecycle transitions.

## Registration lifecycle

Tracked-value registration must happen before `NetworkedItem` finalizes its tracked values. Registration and visual readiness should be treated as separate steps:

- register the tracked integer as soon as `NetworkedItem` and `PageBook` coexist;
- use `PageBook.Generate()`/`PageBookGenerated` only as the barrier for applying the physical page stack.

Waiting until `Generate()` to register is unsafe because render-texture generation can complete after the tracked-value finalization grace period.

The runtime adapter handles both cases:

1. `PagesGenerated` is already true:
   - register immediately;
   - apply any pending remote page.
2. Pages are not generated:
   - register the integer getter immediately if safe;
   - subscribe to `PageBookGenerated` for visual application;
   - retain a pending requested page until generation completes.

Because `PageBook.currentPage` exists before page generation, the getter is safe early. The setter cannot visually apply early because `ForceCurrentPage()` returns while `PagesGenerated` is false.

The implemented setter behavior is conceptually:

```csharp
void ApplyPage(int page)
{
    pendingPage = page;
    if (!pageBook.PagesGenerated)
        return;

    pageBook.ForceCurrentPage(page);
    pendingPage = null;
}
```

When `PageBookGenerated` fires, apply `pendingPage` through `ForceCurrentPage()`.

Only one pending page is retained per book. A newer received value replaces the older pending value, so asynchronous rendering cannot create an unbounded page-state queue.

Do not clamp a pending value against `PageNum` before generation. During asynchronous construction, `PageNum` can still be zero or incomplete. Validate and clamp only after `PagesGenerated` is true and `PageNum > 0`.

Registration and event subscription must be idempotent. A booklet may pass through multiple enable/disable cycles, and generation callbacks must not create a second tracked key or a second `PageBookGenerated` subscription.

Do not finalize the page adapter before it has registered the tracked key. The manager's automatic finalization grace period may be sufficient for ordinary prefabs, but generated job documents should explicitly register as part of their construction path where possible.

## Echo behavior

Incoming tracked values are applied through `TrackedValue<T>.CurrentValue`. Its setter invokes the supplied page setter and then records the received value as `lastSentValue`. Raising `PageFlipped` from `ForceCurrentPage()` therefore should not, by itself, create an object-state echo: after application, the tracked value is clean.

Any additional Harmony patch on `FlipTo`, `ForceCurrentPage`, or `PageFlipped` must remain observational. It should not independently mark the whole item dirty, because the tracked-value getter already detects a local `currentPage` change.

## Authority and validation

Page interaction is not holder-only. Derail Valley lets a player turn a page while a booklet is lying in the world, which is particularly important in VR. A client must therefore be allowed to publish a page change for a known, interactable item even when that client is not the current holder.

The recommended initial ordering model is:

1. local input calls the base game's ordinary `FlipBy()`/`FlipTo()` path immediately;
2. `PageFlipped` exposes the new semantic page to the tracked-value getter;
3. the client sends an `ObjectState` update for the existing nonzero item ID;
4. the host validates the item and page shape, accepts the change into the item's canonical revision order, and relays it;
5. the last host-accepted page change wins if two users turn the same dropped booklet concurrently.

Do not require placement ownership or remote-hand membership. Those checks would reject legitimate table-top and VR interaction. If validation is hardened later, useful checks are that the item is known, the sender is authenticated, the item is relevant/interactable for that sender, and the page index is valid after generation. Exact distance checks should be approached carefully because VR reach and moving coordinate frames can make naive world-distance checks unreliable.

The current generic item validation rejects client `Create`/`Destroy` but does not appear to validate object-state sender possession. That is acceptable for a first page-sync implementation, provided the host still establishes the canonical order and clients cannot use page updates to mutate unrelated item placement or ownership fields.

The page setter should always clamp through `ForceCurrentPage()`. The host may additionally reject impossible indices for clearer diagnostics rather than silently canonicalizing malicious or corrupted data.

### Local and remote animation

Local page input should continue to use the game's normal `FlipTo()` behavior so VR interaction, animation, audio, and all `PageFlipped` consumers behave normally.

For the first remote implementation, `ForceCurrentPage()` is the safest apply method because it deterministically settles every intervening sheet and still raises `PageFlipped`. Once correctness is established, remote application can optionally animate a safe adjacent-page transition with `FlipTo()` and fall back to `ForceCurrentPage()` for late joins, multi-page jumps, interrupted animations, reconciliation, or not-yet-generated books. The semantic result must never depend on the animation completing.

## Full sync and late join

`pageBook.currentPage` must be included in:

- item Create state;
- item FullSync state;
- dirty ObjectState updates after a page turn.

This gives late joiners and relevance re-entry the current semantic page without transmitting page animations or texture data.

Content still needs to be generated locally from the correct item/job identity before the page is visually applied. A received page index should remain pending until `PageBookGenerated` if the content hierarchy is not ready.

## Observability

Recommended events:

```text
item.pagebook-registered
item.pagebook-generated
item.page-change-observed
item.page-apply.before
item.page-apply.after
item.page-apply-deferred
item.page-apply-drained
item.page-index-invalid
item.page-change-submitted
item.page-change-accepted
item.page-change-relayed
item.page-change-superseded
```

Recommended fields:

```text
itemNetId
prefabName
controllerType
currentPage
requestedPage
pageCount
pagesGenerated
runtimePageCount
pendingPage
holderPlayerId
authorityRevision
source: local-input | tracked-state | save-load | initialization
interactionMode: held | world-vr | world-desktop | unknown
pagesReadyAtReceive
canonicalRevision
```

The cross-session semantic comparer should eventually compare:

- `currentPage`;
- `PageNum`;
- `PagesGenerated` only after the generation barrier;
- optionally `pages.Count` after generation.

It should not compare animator time or intermediate page transforms.

Useful warnings are:

- a pending page remains unapplied after `PageBookGenerated`;
- `PagesGenerated` is true but `pages.Count` and `PageNum` disagree;
- the same item registers the page tracked value more than once;
- a received canonical revision is older than the last applied page revision;
- two sessions have matching content identity/page count but settle on different current pages.

## Relationship to the resolved white-sheet bug

`PageBook` creates several intentional renderers and materials, including individual animated sheets, a template, and optionally `BookVolumeModel`. Their enabled/active states are controlled by page generation and animation lifecycle.

The white-sheet presentation bug has now been fixed. The decompiled `PageBookTransparencyHandler` explains why global renderer normalization produced that symptom and records an important invariant for avoiding its return.

When an item is transparent, the handler:

- subscribes to `PageBook.PageFlipped`;
- records the current page;
- enables only the renderer for that page;
- hides the optional `bookVolumeModel`;
- then applies the base transparency operation.

On every page flip it updates the visible renderer set and calls `ForceEndAnimation()` on the newly selected page. When the item becomes opaque again, it enables all page renderers, restores the volume model, and unsubscribes.

`PageFlipped(int)` indexes `pageBook.pages[page]` directly. This reinforces the generation barrier and valid-index requirement: never inject an unvalidated remote index or invoke the event before the runtime page list exists. Applying through `ForceCurrentPage()` after generation provides the required clamp and ordering.

Consequently, a page renderer being disabled can be intentional, transient presentation state. Globally enabling renderers conflicts with the handler, exposes overlapping page sheets, and can present as a blank or white cover over the correct rendered content. PageBook presentation must remain under `PageBook`, `Page`, and `PageBookTransparencyHandler` control. The network layer should apply only the semantic current page and leave renderer selection alone.

## Remaining dnSpy targets

The supplied classes are sufficient for a first page-index synchronization implementation. The following decompilations would refine coverage and avoid missing a variant:

1. Relevant `BookletTextureRender` and `RenderedTexturesBase` methods
   - confirm whether callbacks are guaranteed to run on Unity's main thread;
   - determine whether regeneration can occur on an existing booklet;
   - document base cleanup and render-producer destruction.
2. Base `ItemScrolling`
   - identify the exact scrolling event consumed by `PageBook.Scroll`;
   - document inversion and `CanScroll()` base rules.
3. All callers of `PageBook.ForceCurrentPage`, `FlipTo`, and `Generate`
   - verify that no item type requires an additional refresh method.

The current evidence is enough to implement page-index synchronization across the known booklet and schematic variants. The only useful remaining decompilation target is the base `ItemScrolling` class; the latest attachment contains `ItemScrollingVR` again rather than that base class. It is merely a refinement for input-source attribution and is not a blocker for synchronizing `PageBook.currentPage`.
