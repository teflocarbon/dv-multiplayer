# Items falling through the terrain

Status: measured 2026-10-21 in game (Unity 2019.4, PhysX, DV build 99) while tracking down the
`server-throw` scenario's intermittent "fell through the ground".

**In short:** a mug thrown or dropped onto DV's terrain can go straight through it, with no contact
at all, and keep falling to the water plane under the map (y 110). This is plain PhysX. A bare
rigidbody that has only the mug's convex `MeshCollider` and none of the game's or the mod's scripts
does it too, on a host and on a client. Vanilla has nothing that catches it. `RespawnOnDrop`
respawns an item only once it is too far from the player and from its spawn point. In multiplayer
the mod's fall rescue (`ServerItemManager.FallRescue.cs`, `ClientItemManager.FallRescue.cs`) puts
the item back at its last safe spot.

## What was measured

Mugs were thrown at the spot where `server-throw` lands its mug: beside the track, just off the
ballast, at about absolute (7173, 126, 6889) on terrain `terrain_18`. They were thrown at the
scenario's velocity, with random orientations and offsets of up to a metre. "Fell" means the
mug's centre went 0.5 m under `Terrain.SampleHeight`.

| What was thrown | Impact speed | Fell through |
|---|---|---|
| The `Mug` prefab, every script on | ~9.7 m/s | 14 of 20, 7 to 12 of 15 in other batches |
| The `Mug` prefab with every `Behaviour` disabled | ~9.7 m/s | 10 of 15 |
| The `Mug` prefab, `ContinuousDynamic` or `Discrete` forced | ~9.7 m/s | 9 of 15, 12 of 15 |
| The prefab, dropped from lower | ~8.5, ~7.5, ~6 m/s | 4, 2 and 1 of 20 |
| A bare body with the mug's convex mesh (68 vertices), identity rotation | ~9.7 m/s | 2 of 15, the same on the client |
| A 0.14 m sphere or box | ~9.7 m/s | 0 of 15 |
| The prefab, 4.2 m drops on open ground 50 to 70 m away | ~9.7 m/s | 0 and 2 of 20 |

When it falls through, the trace shows no contact at all. The horizontal velocity is unchanged
and gravity keeps adding 9.81/60 m/s each step. A raycast at the same spot hits `terrain_18`'s
`TerrainCollider` at the surface. `Physics.GetIgnoreCollision` is false. No tunnel portal
(`TunnelCollisionIgnore`) is anywhere near; their ignore sets are empty. The mug body is a 0.3 kg
rigidbody with default contact offset (0.01) and default depenetration, at a fixed step of 1/60 s.

So it depends on the convex hull, its orientation at impact, the impact speed and the terrain
patch. Spheres and boxes don't do it. The mug's hull does, often near the track embankment and
rarely on open ground. Any small item with a convex mesh collider is likely to behave the same.

## What it means for the mod

- Players will see it: a mug thrown off a loco onto the embankment may vanish into the ground. The
  fall rescue brings it back within about a second, to the last spot it was seen resting or held,
  which for a throw is near where it was thrown from, not where it should have landed.
- Scenarios that throw or drop items onto terrain can't treat "it went under the ground" as a
  failure of what they test. `server-throw` judges the throw by its flight above the ground and
  requires the rescue to have happened.
