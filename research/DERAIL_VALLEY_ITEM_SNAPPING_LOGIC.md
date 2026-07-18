# Derail Valley Item Snapping and Train Attachment Logic

This document records established behavior in Derail Valley's modern `SnappableItem` /
`ItemSnapPointBase` system, its coupler and gadget-hosted snap points, and the older independent
`SnapItem` / `SnapItemZone` mechanism.

Generic world authority is designed in
[`MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md`](../MULTIPLAYER_ITEM_WORLD_SYNC_DESIGN.md). Installed
customization gadgets are documented separately in
[`DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md`](DERAIL_VALLEY_GADGET_CUSTOMIZATION_LOGIC.md).

## Sources inspected

- `DV.Items.SnappableItem`, including both special persistence behaviors
- `DV.Items.Snapping.ItemSnapPointBase` and its private `ItemSnapState`
- `DV.CabControls.Spec.SnapPointTypes`
- `DV.Items.ItemSnapPointCoupler`
- `DV.Items.Snapping.ItemSnapPointBelt`
- `DV.Customization.Gadgets.Implementations.SnapPointGadget`
- `DV.Customization.Gadgets.Implementations.GadgetWithSnapPoint`
- `DV.Customization.Gadgets.Implementations.SnapPointGadgetSliding`
- `DV.Items.Snapping.SnapPointAnchorSliding`
- `SnapItem` and `SnapItemZone`
- `EOTLantern`
- `LedBarDriverBase`

Deferred implementation-time research (not an item-world-sync blocker):

- `SnapPointAnchor` and any remaining `ItemSnapPointBase` subclasses;
- all call sites for `SnapItem`, `UnsnapItem`, and `ItemSnappedChanged`;
- prefab usage for legacy `SnapItem` / `SnapItemZone`;
- snap-point destruction and train destruction behavior outside coupler state changes;
- the battery-charger gadget prefab/components and its rechargeable-item snap point.

## Executive conclusions

1. Modern snapped accessories remain their real, active `ItemBase`. There is no inactive backing
   item plus installed projection as used by customization gadgets.
2. `ItemSnapPointBase` reparents through `ItemReparentingBase`, aligns the item's own snap anchor,
   makes its rigidbody kinematic, and optionally disables interaction.
3. Snapping does not generally change storage membership. A player-owned snapped train item can
   remain a `StorageWorld` record while physically parented to the train interior.
4. Coupler persistence uses `TrainCar.CarGUID` plus front/rear coupler identity.
5. Gadget-hosted snap persistence uses the customization identification key plus the installed
   gadget's saved UID.
6. Coupler state is semantic authority: snapping is allowed only while the chain is parked, and
   coupling or leaving the parked state forcibly unsnaps the accessory.
7. A snap point is single-occupancy through `ItemSnapPointBase.SnappedItem`.
8. The older `SnapItem` / `SnapItemZone` system is separate, has no persistence in the inspected
   classes, directly parents to an anchor, and exposes no corresponding unsnap method.
9. `EOTLantern` adds coupler-specific rotation and audio only. `SnappableItem` owns attachment and
   persistence.
10. Belt points are inventory/equipment presentation, not durable world attachment anchors. They
    disable direct interaction and separately reserve an item for the slot.
11. An occupied snap-point gadget blocks normal removal. Forced removal unsnaps its dependent item
    before removing the supporting gadget.

## Snap-point type catalogue

| Type | Value | Established role |
| --- | ---: | --- |
| `Belt` | 1 | Player belt/inventory presentation. |
| `Coupler` | 2 | Front/rear train coupler accessory. |
| `Hanger` | 4 | Gadget-hosted accessory family. |
| `LongCylinder` | 8 | Gadget-hosted/sliding accessory family. |
| `BatteryCharger` | 16 | Rechargeable device placed on an electrically powered charger gadget; absent from the special save-data mask. |
| `StickyPad` | 32 | Gadget-hosted family with synthesized-anchor support. |

`SnapPointTypesToIndex` uses the position of the set bit to index item anchor arrays. A snap point
must contain exactly one bit, while an item may allow several types.

The battery charger is itself an installed gadget and requires wiring/electrical power. Its
`BatteryCharger` snap type is the compatibility category for rechargeable items placed on it. The
type is absent from `SnappableItem`'s special persistence mask, so the generic snapping class does
not record a charger anchor in `ItemSaveData`. The exact behavior may be owned by charger-specific
gadget components or may intentionally be runtime-only; prefab inspection or a save/load runtime
scenario can settle that later without changing the general placement model.

## Two separate snapping systems

### Modern `SnappableItem` system

This system integrates with `ItemBase`, `ItemReparentingBase`, VR/non-VR use, `ItemSaveData`, train
couplers, installed gadgets exposing snap points, item disabling, and type-specific item anchors. It
is the relevant system for EOT lanterns and the train attachments inspected so far.

### Legacy `SnapItem` system

`SnapItemZone` detects a grabbed `SnapItem` entering its trigger. After `Ungrabbed`, if the item is
still inside an unoccupied zone, `SnapItem` makes its rigidbody kinematic, disables collisions,
moves and directly parents it to the zone anchor, records occupancy, and fires `ItemSnapped`.

The inspected classes do not use `ItemReparentingBase`, persist an anchor, restore physics, or expose
an unsnap operation. dnSpy's Used By graph contains only the class's own coroutine and
`SnapItemZone` trigger methods; no other managed code consumes its event. It may still be configured
on prefabs, but remains separate from the modern authoritative protocol.

## `SnappableItem`

`Initialize` binds the `ItemBase` and allowed snap-type flags. It discovers `SnapPointAnchor`
children and stores one anchor per single-bit `SnapPointTypes` value. For sticky pads it may
synthesize an anchor from `CustomNonVrGrabAnchor`.

The following types require or receive `ItemSaveData`:

```text
Coupler | Hanger | LongCylinder | StickyPad
```

Coupler items receive `SpecialSnappedBehaviourCoupler`. Hanger, long-cylinder, or sticky-pad items
receive `SpecialSnappedBehaviourGadgetSnapPoint`.

Non-VR use resolves an `ItemSnapPointBase`, validates `CanSnapCheck`, and calls `SnapItem`. In VR,
trigger hover selects a compatible point and snapping occurs after release. Both converge on the
real snap-point API.

`OnSnapped` records `SnappedTo` and fires `ItemSnappingChanged(item, true, type)`.
`OnUnsnapped` clears it and fires the false event.

## `ItemSnapPointBase` lifecycle

### Validation

The base requires a non-null item, an unoccupied point, a matching allowed type, and `snapAllowed`
unless forced. Derived points add semantic and geometry checks.

### Snap

`SnapItem` validates, force-ends interaction, assigns the single `SnappedItem`, creates an
`ItemSnapState`, animates or immediately finalizes alignment, records `SnappedTo`, and fires the
point and item change events.

Finalization aligns the item's type-specific anchor with the target and makes its rigidbody
kinematic. The item itself remains the visible network representation.

### Parenting and interaction

`ItemSnapState` resolves the destination's train. When train-associated and no explicit
`snapPointTarget` forces another parent, it parents the item to `TrainCar.interior`; otherwise it
uses the target transform. It passes the train rigidbody to `ItemReparentingBase`, captures static
parent state, and remembers the original parent, force source, physics, and collision modes.

During snap it selects speculative collisions, makes the rigidbody kinematic, changes interaction
colliders to the `Inventory` layer, and optionally sets `InteractionAllowed=false` according to
`DisallowInteractionOnSnap`.

### Unsnap

`UnsnapItem` clears occupancy, restores the transient `ItemSnapState`, clears `SnappedTo`, and fires
both sides' change events. The stored restoration parent is runtime-only. Canonical multiplayer
state must explicitly define the post-unsnap placement after save/load or dematerialization.

### Storage-static-parent trap

When snapping to or restoring from a `StorageStaticParent`, `ItemSnapState.ToggleInteraction` can
call `StorageController.AddItemToLostAndFound` for a player-owned item. This explains items
apparently vanishing when they contact the physical Lost and Found shed hierarchy.

It is a special storage-parent safeguard, not general snapping semantics.

## Coupler attachments

`ItemSnapPointCoupler.Initialize` resolves its train and front/rear coupler, reparents the point to
the train interior, adds a non-VR use target, and subscribes to chain and coupling events.

Validation requires the chain to be parked. Leaving parked state or coupling the car forcibly
unsnaps an occupied item. These are train-driven authority transitions, not only player actions.

### Coupler persistence

The coupler behavior writes:

```text
SnappedOnCouplerCarID   TrainCar.CarGUID
SnappedOnCouplerIsFront
```

Load resolves the train through `TrainCarRegistry`, selects the front/rear point from
`TrainPhysicsLod`, clears an unexpected kinematic state, and calls the normal snap method.

If the train or point is missing, vanilla logs an error and leaves the item unsnapped. Multiplayer
needs deferred anchor resolution when attachment data arrives before the train projection.

### EOT lantern

`EOTLantern` observes `ItemSnappingChanged`. For coupler points it rotates a child and plays snap or
unsnap audio, reactivating itself first when necessary. It adds no attachment identity or save data.

## Gadget-hosted snap points

The gadget behavior persists:

```text
SnappedOnHangerCarID   SnapPointGadget.gadgetBase.Custom.GetIdentificationKey()
SnappedOnHangerUIDKey  SnapPointGadget.gadgetBase.UID
```

Despite the historical key names, the behavior is installed for hanger, long-cylinder, and
sticky-pad items.

Load resolves the customization, then its customizer by saved UID, requires a
`GadgetWithSnapPoint`, and force-snaps through that gadget's point. This imposes the dependency:

```text
customization available
  -> installed gadget placed
  -> gadget UID restored
  -> gadget snap point available
  -> accessory restored
```

`SnapPointGadget` keeps the snapped item interactable. Its change handler only plays audio and
imparts random angular velocity to an optional dangler.

`GadgetWithSnapPoint` makes the dependency explicit: normal removal reports no valid removal method
while occupied, while forced removal first calls `UnsnapItem(false)` and then removes the gadget.
Host authority must therefore commit the dependent accessory transition before, or atomically with,
supporting-gadget removal.

### Sliding gadget snap points

`SnapPointGadgetSliding` additionally requires a sliding anchor, a secured `Drillable` parent when
applicable, and unobstructed capsule/sphere-cast space. It calculates a sliding offset, mutates the
item anchor's local position, then calls the base snap method. Unsnap resets the anchor.

`SnapPointAnchorSliding` stores only its configured range and an initial local position captured in
`Awake`; `Reset` restores that position. It has no save callbacks, and no offset appears in the
special snapped-item payload.

During load, the resolved gadget invokes the polymorphic snap point.
`SnapPointGadgetSliding.SnapItem` recalculates the offset from current geometry even when forced,
writes it to the item anchor, and then calls the base method. The offset is recomputed rather than
durably identified, so exact placement may change if surrounding geometry changes between saves.

## Belt snap points

`ItemSnapPointBelt` differs from world and train anchors:

- `DisallowInteractionOnSnap=true`;
- `ReservedItem` is separate from occupancy and fires `ReservedChanged`;
- upright correction applies only when the item opts into `IsUprightInBelt`;
- toggling the point activates/deactivates its parent slot object; and
- snapping into a disabled hierarchy saves the VR interactable's current state.

`Belt` is absent from the special persistent-snap mask, and VR proximity auto-snap explicitly
excludes `ItemSnapPointBelt`. Together with reservation, this identifies belt snapping as a physical
projection of inventory/equipment slot state rather than an independent durable attachment.
Multiplayer inventory authority should drive the belt point and reserved silhouette.

## LED bar clarification

`LedBarDriverBase` converts a value into lit LEDs and supports normal, blinking, filling, and off
animation. It has no `ItemBase`, snap point, train anchor, or save logic.

`GadgetBrakeLEDBarLOD` identifies the actual brake LED bar as an installed customization gadget. It
derives from `CustomizerLODObject<GadgetBase>`, binds a `BrakeWarningChecker` to the gadget's train,
drives the bar from normalized brake-cylinder pressure while powered, and controls a warning lamp.
It is therefore already covered by `InstalledGadget`; it is not a separate snapped or loose train
attachment family.

## Multiplayer authority model

Snapped accessories need an explicit placement state because they remain materialized but have
semantics beyond ordinary train parenting:

```text
SnappedAttachment
  persistentItemIdentity
  snapPointType
  anchor
    Coupler: carGuid + isFront
    GadgetSnapPoint: customizationKey + gadgetUid
    Other: stableSnapPointKey
  optionalLocalSnapData
  placementRevision
```

`Belt` is excluded from this placement because its authoritative identity belongs to the player
inventory slot and reservation model.

The host validates actor authorization, item/point revisions, proximity, occupancy, allowed types,
point geometry, supporting gadget state, and train/coupler state. Clients may preview but cannot
declare occupancy.

Coupling, chain movement, supporting-gadget removal, and anchor destruction can force unsnap. The
host commits these transitions once rather than allowing each client to derive conflicting state.

A snapped train item inherits relevance from its train and nearby observers. It does not churn
through the world-cell index as the train moves. Unlike `InstalledGadget`, snapping does not retire
the item's NetId or create a separate projection.

## Persistence and load ordering

```text
trains and static customizations
  -> installed gadgets and restored gadget UIDs
  -> snapped accessories
  -> wiring and dependent after-load state
```

Missing anchors should remain deferred with explicit expiry/recovery. Vanilla generally logs and
abandons a snap when its anchor is unavailable, which is unsafe when authoritative state arrived
before its train or gadget projection.

## Research sufficiency decision

The unresolved battery-charger implementation, legacy `SnapItem` prefab usage, and destruction edge
cases do not block the general host-authoritative item catalogue.
The placement model already has safe categories for loose train items, installed gadget
projections, persistent snapped attachments, and inventory/belt projections. Unknown snap families
can enter through an explicit stable-anchor adapter when their prefab behavior is encountered.
