# VR Player Avatar — Integration Notes

## Active approach: reuse Derail Valley's own hands (no Unity work)

Remote VR players are shown as a **floating head + hands**, where the hands are **DV's own detailed
hand models cloned at runtime** — so there is **no FBX to import, no rig to configure, and no prefab
to rebuild** for the current version. It's entirely code-side:

| Concern | File |
| --- | --- |
| Pose packets (client→server / server→clients) | `Multiplayer/Networking/Packets/{Serverbound,Clientbound}/*PlayerVrPosePacket.cs` |
| Quaternion serialization | `Multiplayer/Networking/Managers/NetworkManager.cs` |
| Local capture + hand-source registration | `Multiplayer/Utils/VrPoseCapture.cs` (+ `Patches/Player/CustomFirstPersonControllerPatch.cs`) |
| Clone the local hand models (visual only) | `Multiplayer/Components/Networking/Player/LocalVrHands.cs` |
| Client send / server relay | `Networking/Managers/Client/NetworkClient.cs`, `Server/NetworkServer.cs` |
| Mount hands + head, drive from pose, hide body | `Multiplayer/Components/Networking/Player/PlayerHandsController.cs` |
| Apply to avatar | `Networking/Managers/Client/ClientPlayerManager.cs` → `Components/Networking/Player/NetworkedPlayer.cs` |

### How it works
1. In VR, `VrPoseCapture` resolves the local head + hand transforms and registers the hands with
   `LocalVrHands`.
2. Each tick it sends head + both hand poses (in body-local space) to the server, which relays them.
3. On a receiving client, `NetworkedPlayer.ApplyVrPose()` attaches `PlayerHandsController`, which
   **clones the receiver's own local DV hands**, mounts them + a placeholder head under the avatar
   root, hides the walk-animation body, and lerps everything to the networked pose.

### Things to verify in-engine (couldn't be checked offline)
- **Hand transform resolution:** `VrPoseCapture` finds the controller/hand transforms by name from
  the VR camera rig. On first VR use, check `multiplayer.log`:
  - success → `VrPoseCapture: resolved hands left='…', right='…'`
  - failure → it dumps the rig hierarchy; adjust `LeftHandNameHints`/`RightHandNameHints` (or set
    `VrPoseCapture.LeftHandOverride`/`RightHandOverride`).
- **Clone actually contains a mesh:** `LocalVrHands` warns *"cloned hand … has no renderers"* if the
  resolved transform is a bare anchor. If so, point the resolver at the child transform that holds
  the hand `SkinnedMeshRenderer`.
- **Wrist alignment / scale:** hands mount at the pose transform with identity offset. If they float
  off the wrist or are mis-scaled, add an offset/rotation in `PlayerHandsController.Attach`.
- **Head:** currently a primitive sphere placeholder (`PlayerHandsController.CreateHead`) — swap for
  a nicer head mesh when desired.
- **Desktop viewers:** a non-VR client has no hands to clone, so those avatars stay on the walk
  animation. Expected for a VR-first game; revisit if desktop presence matters.

---

## Appendix: full-body + VRIK (future hybrid, not currently used)

An earlier approach rendered a full-body **Quaternius (CC0)** Humanoid driven by FinalIK **VRIK**.
It was removed in favour of the hands route but is preserved in git history (commit `ffbcc24`:
`PlayerVrIkController.cs`, `VrIkTargets.cs`). Revisit this only for a "full body + DV hands" hybrid.

Notes captured while attempting it (useful if resumed):
- Unity **2019.4.40f1**, model under `Assets/Prefabs/Player/Model/` (gitignored).
- The Quaternius **Universal Base Characters** rig has a **broken foot hierarchy**: `Foot.L`/`Foot.R`
  are root-level siblings of the legs, not children of `LowerLeg.L/R`, so Unity's Humanoid rejects
  them ("Foot is not a child of Lower Leg"). Fix in Blender: Edit Mode → select `Foot.L` then
  `LowerLeg.L` (active) → **Ctrl+P → Keep Offset**; repeat right; re-export FBX. Then Rig → Humanoid
  configures green.
- VRIK would be added at runtime mod-side (game ships `RootMotion.FinalIK`); the prefab only needs a
  dependency-free `VrIkTargets` marker (Humanoid Animator + 3 empty IK target child transforms).
