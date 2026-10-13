# Scenarios to rebuild on dvmp

The old runtime test system (in-mod `Debugging/RuntimeTests`, the `RuntimeConsole`, and the
headless `DebugRemoteClient` with its external JSON scenarios) was removed in favour of dvmp.
This is what it covered, as a checklist for rebuilding the useful parts as dvmp scenarios
(`DevTools/Scenarios/<name>/run.sh` + probes). Tick items off as they are ported.

## Items on moving trains (highest value: nothing in dvmp covers this yet)
- [ ] host drops an item inside a moving cab; both peers keep it in the cab (train-relative)
- [ ] client drops an item inside a moving cab
- [ ] host throws an item inside a moving cab; it settles in the cab on both peers
- [ ] client throws an item inside a moving cab
- [ ] cab item "wake" (train item activity): items in an unoccupied cab, re-entry, impact,
      replay - items must not fall through or drift when players leave/return

Needs a dvmp helper to spawn a small consist (loco + car) on a free track near the players and
drive it with real controls (the old "train fixture": create, start/stop auto speed, wait for
motion, relocate, junction route lease, protection from derailment while testing).

## Lost and Found
- [ ] automatic round trip (lost item collected, retrieved by owner)
- [ ] essential item recall (owner recalls a reserved essential item)
- [ ] full inventory on retrieval
- [ ] non-personal items, private owner, other player nearby, owner nearby
- [ ] container shell round trip
- [ ] repeated round trips; stale revision rejected; unknown handle rejected

## Cold containers (item storage)
- [ ] deposit/withdraw round trip, repeated, reopen, stateful items (keep their state)
- [ ] move within a container; nested containers can't form a cycle
- [ ] rejections: foreign player's private container, incompatible item, invalid slot,
      stale revision; private browse

## World items
- [ ] authoritative drop; authored (scene) item projection binding
- [ ] throw settlement, repeated throws, settlement re-projection

## Already covered by dvmp scenarios/tools
- rejoin (+ crash rejoin), save/reload, item soak + desync check, coupling (ad-hoc probes),
  train motion smoothness, handbrake under lag, money pickup, fall-through-map rescue
