# Items falling through the terrain

Status: measured 2026-10-21 in game (Unity 2019.4, PhysX, DV build 99) while tracking down the
`server-throw` scenario's intermittent "fell through the ground".

**In short:** a mug thrown or dropped onto DV's terrain can go straight through it, with no contact
at all, and keep falling to the water plane under the map (y 110). This is plain PhysX. A bare
rigidbody that has only the mug's convex `MeshCollider` and none of the game's or the mod's scripts
does it too, on a host and on a client. Vanilla has nothing that catches it. `RespawnOnDrop`
respawns an item only once it is too far from the player and from its spawn point. In multiplayer
the mod's terrain guard now puts it back on the surface the same physics step
([below](#the-terrain-guard-2026-10-21)); the fall rescue (`ServerItemManager.FallRescue.cs`,
`ClientItemManager.FallRescue.cs`) is the backstop.

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

- Players saw it: a mug thrown off a loco onto the embankment could vanish into the ground. The
  fall rescue brought it back within about a second, to the last spot it was seen resting or held,
  which for a throw is near where it was thrown from, not where it should have landed.
- DV's terrain colliders aren't on the rendered `Terrain` objects (`terrain_18_renderer`): they're
  separate `TerrainCollider` objects in `TerrainGrid`'s pool (`TerrainGrid/terrain_N`, layer
  `Terrain`), plus the host's own `server_terrain_N` around remote players.
- Items are on the `World_Item` layer. A tunnel portal (`TunnelCollisionIgnore`, a capsule trigger
  about 8 m long and 3 m in radius at the mouth) ignores the terrain collider for whatever enters it
  except the `Interactable` layer, so items thrown into a tunnel do go in. Its `TerrainHole` only
  clips the terrain's rendering (MicroSplat); the collider has no hole.

## The terrain guard (2026-10-21)

The mod now guards against it (`NetworkedItemManager.TerrainGuard.cs`,
[design](../legacy/items/item-spatial-physics.md#landing-on-the-terrain)): right after each physics step,
on the game simulating the item, an item that crossed the terrain collider in that step is put back
on the surface where it crossed, without the velocity into the ground.

**Method.** A probe (`dvmp eval`) on the game being measured throws mugs at the `server-throw` spot,
absolute (7173, 126, 6889) on `terrain_18`, the player standing 6 m away: each starts 3 m above
`Terrain.SampleHeight` at a random offset of up to a metre and a random rotation, with velocity
(2, -6, 0) m/s, so it hits the ground at about 9.9 m/s. It counts a fall-through when the mug's
centre goes 0.5 m under the surface within 2 s. The guard is switched off for "before"
(`NetworkedItemManager.TerrainGuardEnabled`), same seed for both. On the host the mugs are
`Instantiate`d from the prefab (networked world items, simulated by the host with no lease). On the
client, a client's local `Instantiate` is removed by the mod, so the host spawns networked mugs by
the client, and the client picks each up, throws it (it then holds the lease and simulates it), and
the probe launches the body from the same start.

| Simulated by | Before | After | Guard corrections after |
|---|---|---|---|
| The host (player host), 40 throws | 11 fell through | 0 | 13 |
| The host, 10 and 20 throws (other seeds) | 3 of 10 | 0 of 20 | 4 |
| The client (lease simulator), 30 throws | 6 fell through, rescued back to where they were picked up | 0 | 12 |
| A dedicated host (the client standing at the spot), 30 throws | 9 fell through | 0 | 9 |

The guard costs about 55 to 65 µs a physics step with about 800 items loaded and none moving (it
looks at every item, skipping kinematic and sleeping bodies). On a player host, `server-throw`
lands its mug at this spot, and the guard put it back on the ground in 5 of 6 runs (on the host for
`ACTOR=host`, on the client for `ACTOR=client`). The scenario now requires the mug never to go under
the ground and never to be rescued. On a dedicated host it lands by the client's home instead, where
the guard never had to act.

An observer's proxy could also dip under the ground: it extrapolates the last motion sample by up to
0.15 s, and a sample taken just before landing carried it on down. On a dedicated host the host's
copy of a client's throw went 0.3 m under once in 7 runs, with no guard correction on the client.
The observer no longer predicts into the terrain.

**Tunnels.** At the portal at absolute (5714, 146, 9145): a mug dropped 40 m inside the tunnel, 25 m
under the terrain surface, and a mug thrown in through the portal at 12 m/s (it ended 5.5 m under the
surface, on the tunnel floor) were neither guarded nor rescued, on the host and with the client
standing in the tunnel next to them. Before, by its code, the fall rescue had no idea of tunnels: it
moved an item more than 1 m under the terrain out of it (not measured). Now neither the rescue nor a
client's report acts on an item with a floor under it.
