# Derail Valley VR Item Logic

> Status: active dnSpy research for the minimum viable multiplayer VR item path.
> This document intentionally excludes finger animation, general VR UI, locomotion, and cab-control
> synchronization unless they directly affect an item held by a player. Remote presence is in scope:
> other players must see a VR user's tracked floating head, visible hands, and held items.

## Goal

Confirm and support exactly these multiplayer behaviours:

1. a VR player can pick up a networked item with the left hand;
2. a VR player can pick up a networked item with the right hand;
3. every peer sees the item in the correct remote hand;
4. an ordinary release becomes the existing host-authoritative dropped-item transition;
5. a physical VR throw becomes the existing host-authoritative thrown/spatial-settlement transition;
6. ownership, recall, inventory, train-interior settlement, and interest streaming remain unchanged.
7. desktop and VR observers can see the VR player's head and both hand models at their tracked poses.

Two-handed item behaviour is out of scope for the first implementation. It must degrade safely to
one authoritative primary hand and must not create two item authorities.

## Existing multiplayer implementation

### VR identity and pose transport

- Login and player-joined packets already carry `IsVR`.
- `LocalPlayerTrackerVR` resolves the left and right VRTK controllers and adds thresholded hand
  position and rotation deltas to `PlayerTrackingData`.
- `PlayerTrackingData` already serializes both hand poses in the ordinary player-position packet.
- `NetworkedPlayer` smooths those targets and drives humanoid left/right hand IK.
- Hand-open flags exist in the wire model but the local VR tracker does not currently populate
  them, and remote presentation does not consume them.

### VR controller bootstrap

`LocalPlayerTrackerBase.Start` waits for `CustomFirstPersonController` before subscribing to network
ticks. An external feature diff claimed that component does not exist in VR. Decompiled code from
the current game build contradicts that categorical claim:

- `CustomFirstPersonController.IsVR` reads `provider.IsVR`;
- `Awake` registers seated-play-area and VRTK callbacks in VR;
- `OnEnable` obtains the VR camera from the character-controller provider;
- `Start` registers the VR teleport callback;
- capsule height, direction, underwater motion, view rotation, and teleport behavior all contain
  explicit VR branches;
- dnSpy `Used By` includes `PlayerTeleportVR`, `LocomotionSetup`, and other VR systems holding a
  `CustomFirstPersonController` reference.

dnSpy reports no managed `new`/`AddComponent` instantiation, but Unity prefab and scene references
do not appear in that result. The most likely interpretation is that the same controller component
is serialized into both player configurations. A live VR component dump remains useful, but the
external diff's statement should not drive the design unless runtime evidence confirms it for a
specific older game build.

### Held-item authority gap

- `ItemUpdateData.Hand` defines `nonVR`, `Left`, and `Right`.
- `ItemUpdateData.PlayerHand` is not serialized or deserialized.
- `NetworkedPlayer.HoldItem(..., rightHand)` ignores `rightHand` and always assigns
  `RightHandItemGO`.
- Remote held-item positioning uses the desktop body/head anchor rather than the synchronized hand
  targets.
- Several cleanup and projection checks recognize only `RightHandItemGO`.

The first implementation therefore needs a serialized primary-hand value and one remote hand
attachment abstraction. It should extend the existing item authority snapshot rather than add a
second VR-specific ownership protocol.

### Existing remote player presentation

The current multiplayer client already instantiates its ordinary humanoid player model for a VR
peer. `NetworkedPlayer.ChangeModel` resolves the model's head and both hand bones, creates a
`NetworkedPlayerIKHandler`, and applies the two transmitted controller poses symmetrically. It also
rotates the model's head from the shared head-pitch field.

This is not a dedicated floating-head-and-controller-hands presentation. There are no current
`LocalVrHands` or `PlayerHandsController` components and no independent HMD position/full rotation
on the wire. A report that only the right hand was visible may refer specifically to the held item:
the pose transport and IK contain both hands, while held-item projection always uses
`RightHandItemGO`.

## Candidate Derail Valley types

### Primary acquisition and release path

| Type | Research purpose |
| --- | --- |
| `DV.VRTK_Extensions.VRTK_InteractGrab_DV` | Controller grab/ungrab state machine, force-grab path, grab events, and controller identity |
| `DV.VRTK_Extensions.VRTK_InteractableObject_DV` | Item-side grabbed/ungrabbed callbacks, current grabbing object, attach policy, and secondary-grab behaviour |
| `DV.VRTK_Extensions.AboutToForceGrabDelegate` | Inventory/equip or scripted force-grab transition ordering |
| `DV.VRTK_Extensions.VRTK_VelocityEstimator_DV` | Linear/angular release velocity and throw application |
| `DV.VRTK_Extensions.VRTK_HandPoseController_DV` | Local visual hand pose only; useful for locating the visible hand transform, not item authority |
| `DV.VRTK_Extensions.VRTK_BaseTwoHandedSecondaryGrab` | Determine how a secondary hand is represented and ensure it cannot become a second authority |

### Secondary telegrab path

| Type | Research purpose |
| --- | --- |
| `DV.VR.TelegrabbableGrabbable` | Confirm telegrab ultimately enters the ordinary VRTK grab lifecycle |
| `DV.VR.TelegrabInteractionHandler` | Identify the real controller passed to the final grab and the release/throw boundary |
| `DV.VR.FakeInteractableObjectProvider` | Confirm it is a production adapter only and does not require separate multiplayer state |

The remaining `DV.VR` telegrabbable control types and UI pointer types are out of scope unless an
ordinary inventory item is shown to use them.

## Confirmed native grab lifecycle

### Controller and primary-hand identity

`VRTK_InteractGrab_DV` is a per-controller component. It owns one `grabbedObject`, and normal and
force grabs both enter the ordinary VRTK `AttemptGrab` sequence. `VRTK_InteractableObject_DV`
retains the primary and secondary grabbing objects. The primary side can be resolved directly with
`VRTK_DeviceFinder.GetControllerHand(interactable.GetGrabbingObject())`; no distance heuristic is
required.

`VRTK_HandPoseController_DV.Initialize` also resolves its side explicitly and obtains the matching
`VRTK_InteractGrab_DV`. It exposes a `visualRoot`, animator, orientation reference, and pose-anchor
hierarchy. Those component references are better hand-model discovery anchors than guessing names
such as `RightHandDV` throughout the VR rig.

### Ordinary item bridge

`DV.CabControls.VRTK.ItemVRTK` is the confirmed VR implementation of `ItemBase`. During setup it:

- adds a `VRTK_ControlImplBaseInteractableObject`;
- subscribes to its grabbed and ungrabbed events;
- configures left and right item snap handles independently;
- installs the appropriate grab-attach and secondary-grab implementations;
- makes the item telegrabbable;
- exposes `GetGrabbingObject()` and implements `IsGrabbed()` through the VRTK interactable.

Most importantly, its callbacks call the shared base lifecycle:

```text
VRTK interactable grabbed
  -> ItemVRTK.OnInteractableObjectGrabbed
  -> ItemBase.FireGrabbed

VRTK interactable ungrabbed
  -> ItemVRTK.OnInteractableObjectUngrabbed
  -> ItemBase.FireUngrabbed
```

`NetworkedItem` already subscribes to those `ItemBase` events and its state detector calls
`Item.IsGrabbed()`. Therefore native VR pickup and release already reach the existing multiplayer
authority observation. The required VR work is hand metadata, correct remote attachment, and
ensuring the release velocity survives spatial-lease handoff—not a separate pickup/drop protocol.

### Normal release and throw velocity

The confirmed ordinary release sequence is:

```text
VRTK_InteractGrab_DV.AttemptReleaseObject
  -> InitUngrabbedObject(true)
  -> grabAttachMechanicScript.StopGrab(true)
  -> VRTK_InteractableObject_DV.Ungrabbed(controller)
  -> controller ungrab event
```

The `true` argument instructs the grab-attach mechanic to apply controller velocity before the
interactable's ungrab callbacks. Therefore the multiplayer release observer should read/preserve
the already-applied native rigidbody linear and angular velocity, then hand simulation to the
existing spatial lease system. A separate VR throw-force implementation is not desirable.

Forced replacement/release uses `ForceRelease(false)` in several paths and deliberately does not
apply throwing velocity. Multiplayer must preserve that distinction.

`VRTK_VelocityEstimator_DV` converts the base estimator's local velocity, angular velocity,
acceleration, and angular acceleration into world space through the estimator's parent transform.
The final base grab-attach implementation is now confirmed. `ThrowReleasedObject(Rigidbody)` reads
the releasing controller's velocity and angular velocity, transforms them through its controller
origin when present, applies the controller and per-item throw multipliers, and directly assigns:

```text
rigidbody.velocity
rigidbody.angularVelocity
```

Items can choose non-physics tracking, controller reparenting, fixed-joint attachment, or a
two-handed pole implementation. All use the common VRTK release contract; derived attach mechanics
must still be checked only if a specific one is found to erase velocity after the base release.

### Two-handed promotion

`VRTK_InteractableObject_DV.Ungrabbed` distinguishes secondary release from primary release. When
the primary releases and the secondary action reports `CanBecomePrimary`, it:

1. clears the old grabbing list;
2. installs the previous secondary controller as the new primary;
3. establishes its track point;
4. calls `BecomePrimaryGrab()`;
5. still emits the interactable-ungrabbed event for the departing controller.

An ungrab callback alone therefore does not prove the item entered the world. The deferred
multiplayer observation must re-check `Item.IsGrabbed()` and the current VRTK primary controller.
This matches the current `NetworkedItem.GetItemState` strategy and avoids a false drop during hand
promotion, but the hand metadata must be refreshed from the new primary.

## Remote head and hand presentation

The desired presentation is a tracked floating head plus both Derail Valley hand models, with a
held item attached to the canonical primary hand.

The external diff's broad concept is useful, but cloning only the observing player's live VR hands
would make VR avatars degrade on desktop observers where that rig may not be instantiated. Research
must establish whether the DV hand visual prefab can be loaded independently from game resources.
If not, the multiplayer asset bundle should provide stable visual-only hand/head prefabs rather
than depending on hierarchy-name searches or mirroring a live controller object.

`TransmogrifyControllers` confirms that local controller visuals are SDK-dependent rather than a
single guaranteed resource prefab. It waits for each SDK controller model, gathers renderers from
the controller `scriptAlias` or SDK model, applies DV materials, and performs Oculus-specific
cleanup. It does expose stable per-side readiness and renderer arrays internally, while
`VRTK_HandPoseController_DV.visualRoot` exposes the hand-pose hierarchy. These are useful live-VR
sources, but neither proves that the same visual asset exists in a desktop observer's scene.

The resource named `[controller_pipa]` is an interaction/attach-point object, not evidence of a
complete reusable hand visual.

The pose transport should remain the existing `PlayerTrackingData` stream. It may be extended with
full local HMD position/rotation, but should not be duplicated by a second every-tick VR packet.

## dnSpy requests

Capture complete decompilation and `Used By` results for:

1. `VRTK_InteractGrab_DV`
   - initialization/subscription methods;
   - grab, force-grab, ungrab, and controller-disable handlers;
   - any override that calculates throw velocity;
   - fields/properties identifying the controller and grabbed object.
2. `VRTK_InteractableObject_DV`
   - grab/ungrab overrides and emitted events;
   - attach/detach transform and rigidbody changes;
   - primary and secondary grabbing-object accessors;
   - `isGrabbable`, allowed-controller, and two-handed policy checks.
3. `VRTK_VelocityEstimator_DV`
   - sampled transforms;
   - velocity/angular-velocity calculation;
   - the method consumed during ungrab/throw.
4. `VRTK_BaseTwoHandedSecondaryGrab`
   - primary/secondary promotion and release ordering.
5. `TelegrabbableGrabbable` and `TelegrabInteractionHandler`
   - the final call that hands an item to the ordinary VRTK grabber;
   - whether release uses the same ungrab and velocity path.
6. The component that binds an ordinary `ItemBase`/`GrabHandlerItem` to
   `VRTK_InteractableObject_DV`, including its grab and ungrab subscribers.

The `ItemVRTK` and base throw implementation are now captured. Remaining presentation research
should identify whether a stable DV visual-only hand prefab can be loaded without an active VR SDK
controller. Remaining physics research is limited to concrete derived grab-attach implementations
if live testing shows velocity loss.

Also capture dnSpy `Used By` for the ordinary item bridge's grab and ungrab callbacks. That bridge
is the most important seam: if it emits the same `ItemBase`/inventory events used by the existing
multiplayer observer, local authority transitions may already work and only hand identity plus
remote presentation need changes.

## Questions to answer

### Pickup

- What exact callback first means that a VR hand owns an ordinary item?
- Can the grabbing controller be mapped deterministically to left or right?
- Does the item remain a child of the controller, an attach point, or another hand model transform?
- Which ordinary `ItemBase`, inventory, or `GrabHandlerItem` events are emitted?
- Does force-grab from inventory use the same callbacks?

### Drop

- What exact callback means ordinary ungrab has completed?
- At that point, has the rigidbody already been detached and made dynamic?
- Does the shared multiplayer item observer see `Dropped` without VR-specific state mutation?

### Throw

- Where are controller linear and angular velocities sampled?
- Which component applies them to the item rigidbody?
- Is there a distinct throw callback, or is a throw simply an ungrab with sufficient velocity?
- When should multiplayer capture the initial spatial sample so it does not erase the native VR
  impulse?

### Both hands

- Is left/right available directly from the controller SDK alias, or must it be inferred from
  `VRTK_DeviceFinder` transforms?
- What happens when the secondary hand grabs an already-held item?
- What happens when the primary hand releases while the secondary hand remains?
- Can the implementation retain one authoritative `PlayerHand` through a primary-hand promotion?

## Intended multiplayer boundary

The preferred flow is:

```text
native VR grab completes
  -> observe ordinary item lifecycle
  -> submit existing PlayerHand authority transition with Left/Right metadata
  -> host validates and broadcasts canonical holder + primary hand
  -> remote projection attaches to that player's synchronized hand target

native VR ungrab completes
  -> preserve DV's detached transform and rigidbody velocity
  -> submit existing Dropped or Thrown transition
  -> existing spatial lease/settlement system takes over
```

VR code must not directly invent world poses, bypass the authoritative item registry, or create a
parallel throw protocol.

## Initial acceptance matrix

Run each direction with both a VR host and a VR client where practical:

| Action | Required observation |
| --- | --- |
| Pick up with left hand | holder sees native item; peer sees item on left hand; one canonical holder |
| Pick up with right hand | holder sees native item; peer sees item on right hand; one canonical holder |
| Drop from left hand | item detaches, becomes dynamic, settles once, and remains recallable |
| Drop from right hand | same invariants as left |
| Throw from left hand | native velocity survives authority transition and spatial settlement |
| Throw from right hand | same invariants as left |
| Swap primary hand | no duplication, destruction, ownership loss, or false world drop |
| Drop/throw inside train | existing train-relative settlement and wake system remains authoritative |
| Leave and re-enter interest | held or settled item reprojects once with correct owner and hand |

## Runtime finding: displaced hand tools

The 2026-07-20 two-player log exposed an ordering race with flashlights. Flashlight NetId `749`
was projected into the remote right hand, then an oiler was projected into that same hand before
the flashlight's separate `InInventory` snapshot arrived. The old replacement path released the
displaced item into the world. If the later placement packet was delayed or lost, the flashlight
could remain visible on or beside the avatar indefinitely while its independent `buttonState` and
`batteryPower` updates continued. That explains the apparently contradictory report that the
flashlight still worked while its model was stuck or invisible.

Remote hand replacement now treats the new held item as proof only that the previous item no
longer occupies that hand. It parks the displaced projection in the remote inventory (inactive)
until that item's own canonical placement snapshot arrives. Every authoritative hand/inventory
snapshot also clears that item from all remote hand slots before applying its sole destination.
An explicit later `Dropped` or `Thrown` snapshot still restores the item to the world normally.
