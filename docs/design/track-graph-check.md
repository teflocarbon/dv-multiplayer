# Track-graph check at join

Status: built, 2026-10-20. From [research/mapify-test-map.md](../research/mapify-test-map.md) §3
item 3 (the Mapify trial itself was dropped; this check was worth having on the vanilla map).

## Problem

Rail tracks, junctions and turntables aren't given NetIds by the host. Each game numbers them
itself as they wake (`IsIdServerAuthoritative => false` on `NetworkedRailTrack`,
`NetworkedJunction` and `NetworkedTurntable`; see [architecture.md](../architecture.md#networked-components-and-netids)).
Every bogie position, car spawn, rerail and move-to-track names a track by that NetId, and the
join's junction and turntable state is an array in NetId order. Two games agree only if they built
the same track graph in the same order.

On the vanilla map they do, because both load the same scene the same way. A client whose graph
differs (another mod that adds, removes or renames track; a different scene; a Mapify map) used to
join without any error and get every car on the wrong track and the wrong switches thrown.

## Check

Each game hashes its NetId → GameObject-name tables once its world has loaded:

- `Multiplayer.Core/World/TrackGraphFingerprint.cs`: FNV-1a 64 over the three tables (tracks,
  junctions, turntables, in that order), each sorted by NetId then name. Every table starts with
  its kind and entry count; every entry is its NetId and its name, length first. Names are hashed
  as UTF-16 code units, so the hash doesn't depend on the process (`string.GetHashCode` would).
  The fingerprint also keeps the three counts, for the reason shown to the player.
- `Multiplayer/Shared/World/TrackGraph.cs` collects the tables from each component's NetId
  registry (`IdMonoBehaviour.Registered`, exposed as `NetIdTable`).

The join:

1. The client computes its fingerprint in `NetworkClient.SyncWorldState`, after
   `WorldStreamingInit.LoadingFinished` and after the mod ready-blocks clear (so a mod that adds
   track has done so), and sends it in `ServerboundLoadStateUpdatePacket`
   (`TrackGraphHash`, `TrackCount`, `JunctionCount`, `TurntableCount`) with `ReadyForWorldState`.
   Every other state sends zeros.
2. The server keeps it on the `ServerPlayer` (a join held in `joinQueue` while the host loads is
   replayed without the packet). Once loaded, before anything else at `ReadyForWorldState` (before
   the player joins `peers`, so before any world state, broadcast or train baseline), it computes
   its own fingerprint and compares (`NetworkServer.CheckTrackGraph`). The host's own client
   shares the server's world and is skipped.
3. On a mismatch the server logs both fingerprints and kicks the player with
   `ClientboundDisconnectPacket { Kicked = true, Reason = ... }`. The client shows the reason
   instead of "You were kicked!". The reason says whether the counts differ (and gives both) or
   whether the same objects are numbered differently, and points at mods that change track.

Both games log their fingerprint (`Track graph: <hash> (<n> tracks, ...)`: the server at
`OnLoaded` and on each check, the client before `ReadyForWorldState`), so two logs can be compared
by eye.

Every client is checked, on a player-hosted game and on a dedicated host alike. Host and client
must already run the same mod build (the protocol fingerprint), so there is no older client that
sends no graph.

## Not covered

- **Which tables differ, and where.** The client sends one hash, not the tables, so the reason
  can't name the first differing track. Logging both full tables on a mismatch would need the
  client to send them (a few thousand names); not worth it until a mismatch is seen in practice.
- **Other locally numbered objects.** Stations, pit stops, cash registers, pluggables and
  warehouse controllers are numbered the same way and aren't in the hash. Stations are keyed by a
  hash of their ID already; the rest would follow the same pattern if they ever desync.
- **Graphs that change after load.** The host computes its fingerprint at each join, the client
  once at load. A track created or destroyed at run time would already break the NetId scheme;
  the check would then refuse joins instead of hiding it.

## Testing

- Unit: `Multiplayer.Core.Tests/World/TrackGraphFingerprintTests.cs` (order independence,
  swapped NetIds, extra or renamed tracks, table separation, name boundaries, a fixed expected
  hash).
- Protocol: `Multiplayer.Protocol.Tests/TrackGraphPacketTests.cs` (the new packet fields and the
  disconnect reason round-trip).
- In game: any join exercises the matching path (`rejoin`, `save-reload`, on a player-hosted and a
  dedicated pair); the host's log should show `track graph matches`. The refusal path has no
  scenario: it needs a client with a different track graph.
