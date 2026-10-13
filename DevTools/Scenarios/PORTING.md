# Scenarios to rebuild on dvmp

The old runtime test system (in-mod `Debugging/RuntimeTests`, the `RuntimeConsole`, and the
headless `DebugRemoteClient` with its external JSON scenarios) was removed in favour of dvmp.
This is what it covered, as a checklist for rebuilding the useful parts as dvmp scenarios
(`DevTools/Scenarios/<name>/run.sh` + probes). Tick items off as they are ported.

## Items on moving trains
- [x] host/client x drop/throw x train stopped/moving: `train-items` scenario
      (`WHO=client|host MODE=drop|throw WHEN=stopped|moving`). All variants pass.
- [ ] cab item "wake" (train item activity): items in an unoccupied cab, re-entry, impact,
      replay - items must not fall through or drift when players leave/return

Train helpers live in `Probes/lib/Trains.cs` (spawn near the players, board a cab, push the
consist, delete). Not yet rebuilt from the old train fixture: driving with real loco controls,
junction route leases.

## Lost and Found
- [x] automatic round trip (lost item collected, retrieved by owner): `lost-and-found` scenario
- [x] essential item recall (owner recalls a reserved essential item): `recall` scenario (`TAKEN=1`:
      from another player's hands)
- [ ] full inventory on retrieval
- [ ] non-personal items, private owner, other player nearby, owner nearby
- [ ] container shell round trip
- [ ] repeated round trips; stale revision rejected; unknown handle rejected

## Cold containers (item storage)
- [x] deposit/withdraw round trip, reopen, stateful items (keep their state), foreign browse
      rejected: `containers` scenario (`CONTAINER=` `PREFAB=`); graph rules (cycles, stale
      revisions, foreign items, quotas) are covered by Multiplayer.Core.Tests ColdContainerGraphTests
- [ ] move within a container; nested containers can't form a cycle
- [ ] rejections: foreign player's private container, incompatible item, invalid slot,
      stale revision; private browse

## World items
- [ ] authoritative drop; authored (scene) item projection binding
- [ ] throw settlement, repeated throws, settlement re-projection

## Already covered by dvmp scenarios/tools
- rejoin (+ crash rejoin), save/reload, item soak + desync check, coupling (ad-hoc probes),
  train motion smoothness, handbrake under lag, money pickup, fall-through-map rescue
