# Playtest tracker

The working list for playtests with friends on the dedicated server: what broke, what's fixed, and
what's still open. Update it in the same commit as a fix. Anything still open after a few sessions
should become a GitHub issue instead (as [#76](https://github.com/teflocarbon/dv-multiplayer/issues/76)
was).

**Status words:**
- **Fixed, tested:** checked in a game or a dvmp scenario.
- **Fixed, untested:** committed and builds, not yet run in a game.
- **Open:** cause known, not fixed.
- **Needs a repro:** cause not known yet.

## Session 1: 2026-10-17, dedicated server, 3 players

**Setup:**
- Build: release 2026.10.17.1, from `dev/player-state` at 083f39c0 (the merge of
  `dev/world-terrain`).
- Server: a dedicated host on the owner's PC, under the watchdog (`run-dedicated-host.ps1`). The
  owner joined from VR on the same PC, with two friends over Steam.
- Logs:
  - Server: `%LOCALAPPDATA%\dv-multiplayer\dedicated-host\logs\`.
  - Players: `%USERPROFILE%\AppData\LocalLow\Altfuture\Derail Valley\Player.log`, or
    `Player-prev.log` after a restart.
  - Debug Logging is off by default: turn it on in the mod settings before a session. The server
    reads it at start.

**Not in that build:** every fix below was committed after it. They're all in **release
2026.10.17.2**, built from `dev/player-state` at eedb3689 at the end of the session. Every player
installs it, since some of the fixes run in players' games. It went out without the pre-release
checks below, so most fixes are first tested by playing it.

### Fixed

| Problem | Cause | Fix | Status |
|---|---|---|---|
| The owner was kicked whenever another player joined or left | Steam reported the owner's ticket cancelled (the server and the owner's game share one Steam account), and the late-rejection check kicked them | A player Steam has confirmed is kicked later only for a ban or a lost licence (`PlayerAuthentication`, 82ddb491) | Fixed, untested |
| S282: compressor, dynamo, cylinder cocks, lubricator and oiling did nothing; DE6: a friend's open window showed only for them | These controls never raise Grabbed or Used, so nothing took their control lease. The player's game dropped the change as feedback, and the server refused unleased writes anyway (#73 covered only buttons and switches) | Players' games send a control change that nobody else holds a lease on; the server takes it (`NetworkedTrainCar`, `NetworkServer`, 69d0548a). `cab-controls` gained a case with `LOCOS="LocoS282A LocoDE6"` | Fixed, untested |
| A client's radio and maps came back lying in the world on a dedicated host | Items without a slot claim took the slots the essentials reserve | An item without its own claim on a slot moves aside for the essential that reserves it (`InventoryIntegration.MakeRoomForClaim`, 1e101ef4) | Fixed, tested |
| A carried item a player found in the world (a mug) was gone after a reload | The save kept only items DV marks as the player's own | Anything in a player's inventory or hand is saved (4844d6d6) | Fixed, tested |
| A crashed player lost five sandbox starting items (boombox, handheld console, EOT lantern, stopwatch, remote) | Starting items a player's own game makes stay unbound until first used, so only that game knew them | Ten seconds after the loading screen, the player's game asks the server to adopt what's still unbound in its inventory (375eb1c5) | Fixed, untested |
| A radio left in a train became unreachable once the car was deleted, and the server looked up the missing car 30 times a second | Items in a deleted car went to the car pool with it | Before a car is deleted, a player's own items in it go to their Lost and Found, and anything else goes with the car cleanly (b503fabc) | Fixed, untested |
| Ground fire burnt for 20 minutes with no fuel | A vanilla bug: a burning tile's list of flammable liquids is rebuilt empty, or never rebuilt, so it neither burns its fuel nor goes out | The server rebuilds an empty list and clears traces (`HazmatSync.PutOutBurntTiles`, bb890858) | Fixed, tested (applied by hand to the live server: all 31 tiles went out) |
| A damaged tank car leaked from different places on each screen ([#76](https://github.com/teflocarbon/dv-multiplayer/issues/76)) | Each game rolled its own random rupture point | The pick is seeded from the car's ID (`CargoLeakPointPatch`, 1f2e53be) | Fixed, untested |

### Added for running the server

| What | Commit | Status |
|---|---|---|
| The watchdog's window shows the mod's log, and takes server commands (they run as the owner) | 3dd2e0e9 | Untested |
| `/give <player> <item> [count]` and `/restore <player>`, straight into the inventory | a8bbdf78 | Tested by hand on the live server (a mug) |
| `/weather clear\|rain\|storm [hours]`, `/weather auto`, `/weather new` | 52196852 | Untested |
| `/time HH:mm` and `/time +hours` (forward only) | a6f18314 | Untested |
| `/repair [player] [all]` | 0372e8d4 | Tested by hand on the live server (a DE6 with dead traction motors) |
| The server runs from its own folder, with its own mods and settings; the watchdog and `Start Dedicated Server.cmd` ship with the mod | a2ac773d, and its own copy of Unity Mod Manager in the commit below | Tested: the server started twice, loaded all 7 mods from its own folder, created its lobby and stopped cleanly |
| "DV Multiplayer Server.exe": a window for the setup (save, server settings, admins, which mods) and for running the server (log, status, commands), in place of the script | see the commit below | Tested: started the live server for session 2 (2026-10-17), which loaded its own mods and settings through its own UMM, created its lobby, and the owner joined |
| `/season spring\|summer\|autumn\|winter\|next\|blizzard\|calm`, with the [Dynamic Seasons](https://github.com/HerodrineYT/DVSeasons) mod (0.4.0, which syncs itself through the Multiplayer API, the host as its authority) | see the commit below | Untested |

### Open

| Problem | What's known | Next step |
|---|---|---|
| Friends-only servers don't appear in the server browser | The browser asks Steam only for public lobbies, and Steam never returns friends-only ones. This affects every host, not just a dedicated one | Also list friends' lobbies. Meanwhile, use `-Visibility public` with a password |
| The whistle sometimes stuck | Its lease is taken and released over and over, so a late or out-of-order release can leave it stuck | Make a release always reach everyone |
| A friend couldn't pick up one job overview (#800, refused 5 times between 08:51 and 09:04 UTC); their map pickups worked | The server refuses it, and a Release server doesn't log why | Repeat with server Debug Logging on and read the refusal reason |
| Duplicate item numbers on job overviews at FRS ("Rejected duplicate NetId") | Seven of a client's own overview copies kept number 0, so nothing could be done with them | Find who creates the second copy |
| Items an admin put on a train's floor couldn't be picked up | The train-item system never announced them to anyone. `/give` now uses the inventory, but the gap remains | Have the server announce items it places in a train |
| Licence papers aren't covered by `/restore` | Only starting items are | Add licences (`LicensesSafeguard`) |
| No refused-ticket test | It needs a second Steam account | Check by hand when one is available |
| No `career-convert` scenario | Converting an old career once is untested | Write the scenario |
| The watchdog script has no "server ready" line | Players have to watch the log for `[Server] Server loaded` | The server app shows "players can join"; the script still doesn't |
| The fees screen doesn't work from a client | — | `/payfees` covers it meanwhile |
| Session 2: a radio was thrown away on load ("Discarding persisted world item CommsRadio at height 5002") | `WorldItemPersistenceRestore` skips World items above 1500 m as old-build leftovers, but the current build still saves some inventory items as World at the hidden inventory height | Find which path saves a carried item as World; restore such items into the owner's inventory instead of discarding them |
| Session 2: items clipped to the VR belt ("carabiner") fell to the floor and couldn't be picked up | Belt slots are inventory slots, but DV shows the item snapped to the belt. The mod doesn't know the belt, and the server recorded the items in the inventory of player 0 at the clipping spot. Replaced live with `/give` | Being fixed in its own session (VR belt sync). Meanwhile, VR players shouldn't use the belt |
| Session 2: one VR player couldn't switch junctions remotely (comms radio or map): the switch never highlighted; by hand it worked, and remote switching worked for another VR client | Server records for his radio and maps were right, `JunctionSwitching` was allowed with no whitelist, and no patch of ours touches `JunctionSwitcher`/`CommsJunctionSwitcher`. Re-taking the radio didn't help | Rejoin; if it persists, his Player.log, and check whether his switcher is enabled (`JunctionSwitcherManager.activeSwitchers`) and what its raycast hits |
| Session 2: a dropped lighter disappeared | In the player's game it was listed in DV's Inventory but still active, parented to the FEF-3's interior and 18 m below the player (fallen through); the host had it in his inventory. Fixed live with `RevokeMembership` then `AddActive` | Find how an item ends up in the inventory list without being stowed (a drop and an inventory add racing?) |
| Session 2: a client's wallet showed $300,000,224 against the host's $300,000,234 | `ClientboundMoneyPacket.Amount` is a float: large balances round to about $32. Display only (the host holds the balance) | Send it as a double |
| Session 2: a VR player's glove went pink | Dynamic Seasons gives vehicle renderers its snow material, and a VR player's hands are parented to the loco they're in, so it took the gloves too: one glove had its snow material, the other none (pink). Fixed live by putting `M_MechanicGloves_Yellow` back. Not ours | Report to Dynamic Seasons (skip renderers under `[CameraRig]`); or put the glove material back after a season change |
| Session 2: Dynamic Seasons sends about 50 KB of season state every 5 seconds | Three `SeasonStatePacket`s (24, 24 and 16 KB before compression) | Ask its author to send it only on change |

### Needs a repro

| Problem | Leads |
|---|---|
| The handcar's crank feels glued | The crank is linked to the wheels, and a client's copy of the car follows the server's position. Or it's the lease, if it only sticks with two people on it. Ask whether it sticks with one person |
| Recall didn't fetch a radio left in a train | The client had the item, but after its rejoin the slot probably had no ghost, and so no **Get** button. Leave a radio in a cab, move away, rejoin, try **Get** |
| Trains hop up and back down onto the track | Either the "only move where there's ground under both bogies" check, or correction jumps after gaps in updates ("large gap between snapshots: 141 ticks"). Note the time and the train |
| A player's game crashed natively (Burst code) 0.3 s after cars around them were deleted | They were standing in one of the deleted cars. Their crash report is in `%TEMP%\Altfuture\Derail Valley\Crashes` |
| The S282's mechanical lubricator handle didn't move at all | The lease fix covers its port, but not a handle that won't move. It may be VR interaction |
| Manual oiling | Confirmed working in session 2 |
| A derailed tender thrashed in one client while the host had it still (session 2) | A pile-up of about 12 derailed cars; the FEF-3 and its tender overlapped. On the host everything was at rest; on the client the tender sat 2 m off, inside the loco, at 21.6 m/s and spinning. A client's derailed car already turns kinematic at the host's pose once the host has it at rest (`NetworkedTrainCar.UpdateDerailedKinematic`), but only after the client's copy has reached that pose (0.3 m, 10°). Wedged inside the loco it never could, so it stayed dynamic, fighting the corrections. Rerail or delete clears it | When the host has had it at rest for a few seconds and ours still hasn't reached its pose, snap it there and make it kinematic anyway |
| A DE6 (L-056) had no brakes (session 2) | First its compressor was destroyed (health 0), so there was no air; `/repair` fixed that. Then, engine running and reservoir empty, its compressor still never ran: `compressor.ACTIVATION_SIGNAL_EXT_IN` was 0 on every game though the brake system wanted it on. The port is typed CONTROL but set by `CompressorSimController` only when the brake system's decision changes, so once something wrote 0 (most likely a client's own copy, sent as an unleased control change) nothing put it back. Fixed live, and in code: clients never send it, the host refuses it and keeps it matched to `brakeSystem.compressorActivationSignal` (`NetworkedTrainCar.Server_KeepCompressorSignal`). Untested in code. Also seen: `brakeCutout.EXT_IN` 1 on the server and 0 on a client until the repair; other ports set by DV code but typed CONTROL may have the same problem. The dynamic brake at full revs a standing DE6 to full RPM: vanilla, not a bug |
| The firebox coal flickered on an S484-840 (a mod's loco, with a stoker) for a while, then settled (session 2) | On the server the firebox stayed full and the tender drained smoothly. The host sends state ports like `firebox.COAL_LEVEL` while each game also simulates the firebox, so with the stoker topping it up at full the local value and the corrections may take turns. Watch for it on a client with the dev tools loaded |
| "Items fall through the map" | Never reproduced. Needs the original circumstances |
| "World item state save failed for <id>: Object reference not set to an instance of an object" for items other than flashlights (82 times in the playtest's server log, 3 in a test start with nobody online) | Flashlights are fixed (session 2, below). Tonight's server logs also name 733 (84 times), 737, 754, 755, 770 and 772; numbers are reused between runs, so these aren't identified yet. The warning has its stack now: read it after the next save |
| Memory per player, and the cost of the extra Far tiles on a player host | Not measured |

### Before the next build goes out

1. Run `cab-controls` with `LOCOS="LocoS282A LocoDE6"` on one pair.
2. Try `/weather`, `/time` and `/repair` once each.
3. Do the recall repro, and check a leaking tank car from two games.
4. If `fix/belt-items` is in: in VR, clip a lantern and a flashlight to the belt. They stay on the
   belt, no other game shows them on the floor, and the server's record is `PlayerInventory` of
   that player (`AuthoritativeItemRegistry`, not `PlayerInventory` of player 0). Take one off the
   belt into a hand and drop it; leave, rejoin, and check the other is back in the inventory. Save
   with nobody online and check for "World item state save failed" on a flashlight.
5. Build Release. Every player installs the new zip.

## Session 2: 2026-10-17 evening, dedicated server, 4 players

**Setup:** release 2026.10.17.2. The server ran from its own folder, with its own copy of Unity Mod
Manager and 20 mods (a2ac773d, d4dabf7f), under the new server app. The dev tools ran on the server
only, so fixes were made live with `dvmp_eval`: Harmony patches applied to the running game, and
items put back with `AdminItemGrants.PutInInventory`. The code fixes are in the commit after this
list. They build and the core tests pass, but they haven't run in a game.

| Problem | Cause | Fix | Status |
|---|---|---|---|
| A player couldn't pick up job overviews another player could; they jumped back to the table | A client's "I have it" acknowledgement was accepted only at the item's exact revision. Every game near a new overview sends an update as it appears, which moves the revision on before a slower client's acknowledgement arrives, so it was dropped and that player's pickups refused | Accept an acknowledgement at or below the record's revision (`NetworkServer.OnServerboundWorldItemProjectionAckPacket`) | Fixed live; code fixed, untested |
| A VR player's item vanished from their hand: a lantern as it was lit, an EOT lantern, a flashlight | The server's copy of the item mirrors the player's. Its observation of that (taken in a hand, lit) was applied as a change by the server's own player, which booked the item to the server's parked body; the next update moved it there | The server's own observations of another player's hand or inventory keep only their object state (`NetworkedItemManager.TryKeepRemotePossession`). A backstop puts a record that still lands on the server's player back with the player whose item it is (`AuthoritativeItemRegistry.KeepRemotePossession`): one path that did it wasn't found, and the backstop logs when it acts | Fixed live; code fixed, untested |
| A lost radio blocked `/give` ("already has a CommsRadio somewhere") and DV's own safeguard, so the player had none | `OwnedItemsSummary` also matched the per-session player number, so another player's saved radio counted. A saved Lost and Found entry that can never come back (DV's Lost and Found storage was empty) also stayed pending forever, unseen but owned | Owners are matched by identity, and by number only for a record without one. A pending Lost and Found entry binds to any free stored item of its kind, or is dropped with a warning | Fixed live; code fixed, untested |
| Log noise: "Sending CommonItemUpdatePacket" about 10 times a second per player, a 13-line dump per item in bulk updates, "Queuing", and two lines per saved item at every login | — | Removed (they're in the debug trace) or moved to Debug; an item with no player at all stays a warning | Fixed, untested |
| "World item state save failed" had no stack trace | — | The warning includes the stack now | Fixed, untested |
| A VR player's frame rate sank again (18 fps with the hands in view) | The #78 leak: 80 near-touch collider containers under the controllers instead of 2. Its fix (1ab09ad3, PR #89) went to beta after 2026.10.17.2 was built from `dev/player-state`, so the release never had it | Cherry-picked into `dev/player-state` (46ea72af). Cleared live in one client (78 destroyed: 18 → 45 fps); the other VR players can't be cleared without dev tools, and opening the SteamVR dashboard adds two more each time | Fixed live for one player; in code, as tested in #78 |
| Closed oiling caps on a 2901 reopened 10-15 seconds later | A client drops a control change made within 1.5 s of the host's last value for it as echo, and for good. Every game that loads a car's interior asks for a full resync of it, and a player at the loading distance reloaded it every 5-15 s, two requests a time; each resync refreshed every port's "last host value" time on every game, so closes fell in the window, never reached the host, and the next resync reopened them. Shown live: a close sent from a client with dev tools went through and stuck | A change still settling waits and goes if it still differs (`Client_UnleasedChange`); interior-load resyncs are one per car per 10 s (`Client_RequestInteriorResync`) | Fixed live (caps closed on the host); code fixed, untested |
| Stoker locos (S484, 2901): firebox permanently full, overfill damage climbing, coal flickering | Coal added was reported by watching the firebox's coal input port, which Custom Car Loader's `SteamMechanicalStoker` writes every tick on every game: every game reported its stoker (47,000 reports in this log), the host's own client too, and the host added them all on top of its own stoker | Report only `FireboxSimController.TransferCoal` (shovelling), from clients only (`FireboxTransferCoalPatch`) | Code fixed, untested |
| A DE6's compressor never ran: no air, no brakes | See "A DE6 (L-056) had no brakes" under Needs a repro | `Server_KeepCompressorSignal` (d66aca55) | Fixed live; code fixed, untested |
| After a rerail, a client's copy of the loco slides and flings ("the wheels lose it, the top half detaches") while the host has it still (FEF-3 L-069) | Recorded 6 s from the rerail on host and client: the host's car was at rest at the new spot; the client's slid off at a steady 1.5 m/s, then was snapped back (5-6.9 m/s, spinning), every ~1.3 s, forever. Nothing on the client that follows the host was reset by a rerail. The rigidbody queue (`NetworkedRigidbody`, for derailed cars) kept steering the body towards the car's last derailed pose at its 1.5 m/s cap, on the rails; the speed queue's placement (`Placing`) slid it back towards the host's spans, and each 2 s full sync found it over 5 m off and snapped it, flinging the body off its bogies. The bogie queues also kept samples from before the derail, which a stopped car's follower never treated as stale, and the rerail ran on a kinematic body (a derailed car at rest is kinematic on clients). The same applied to a host move-to-track | A host rerail or move-to-track clears the client's speed, rigidbody and bogie queues before and after vanilla relocates the car, makes the body dynamic first, ignores samples while the car is teleporting, then asks the host for a full sync (`Client_RerailFromHost`, `Client_MoveToTrackFromHost`). The rigidbody queue only steers a derailed car; a derail clears the bogie samples; a stopped car's bogie sample is stale after 10 s. The host sends the car's pose and bogie tracks reliably once its own relocation is done (`Server_ResyncAfterRelocation`). Scenario `rerail` (`WHO=client`, as the comms radio does, and `WHO=host`) | Fixed, untested |
| Other CONTROL-typed ports that DV or a mod's simulation sets could go the same way as the compressor's signal (`brakeCutout.EXT_IN` differed between games) | Players' games sent any CONTROL port that differed from the host's as a player's unleased change. An audit found many no player drives: `horn.HORN`, `reverser.REVERSER`, `throttleCalculator.LAYSHAFT_POSITION`, `tractionGenerator.THROTTLE`, the DM3's `gearInputA/B.GEAR` and `REVERSER_OUT`, the headlight and wiper decoders' ports. Each is derived from a control's own port | Each car's player-driven ports are read from its livery's prefabs (cab, car body, exploded versions): the ports of its `InteractablePortFeeder`s, oiling points, port keyboard inputs and handcar handle, plus any control hooked in a loaded cab. Clients send only those; the host takes only those, or one the sender holds the lease on. The compressor's signal is the one CONTROL port the host keeps (`ControlPortWriters`, `InteractablePortScan`) | Code fixed, untested |
| A VR player clipped a lantern and a flashlight to the belt; they fell to the floor in their own game and nobody could pick them up. The server had them as `PlayerInventory` of player 0 at the spot they were clipped, and other games logged "Could not project item N to player 0" | Two things. (1) The belt takes a released item only at the end of the frame, so the frame in between sent `Dropped`, then `InInventory`: the server gave the item to the world, and the inventory update behind it was refused or arrived out of turn. (2) Applying the server's echo of the owner's own `InInventory` unsnapped any snapped item first, belt included, so it fell while staying in the inventory slot. Player 0: the server clears the holder's per-session number on every record when a player disconnects (`UnbindPersistentOwner`) | A VR release keeps its last state until the next frame, and an item on the local player's belt that a snapshot puts in their own inventory stays snapped (`BeltItemPolicy`; "VR belt items" in `docs/design/item-world-sync.md`). Others see it as an inventory item (hidden), not on the avatar's belt (`fix/belt-items`) | Fixed, tested (VR, session 3; one more case found there) |
| "World item state save failed for <id>: Object reference not set" for flashlights | A flashlight that has never been active hasn't run `Start`, so its save handler has no button yet. The server keeps other players' inventory items inactive | Save the battery and the loaded switch state when the button isn't there yet (`FlashlightItemSavePatch`, `fix/belt-items`) | Fixed, untested |
| Passing a held item from one VR hand to the other sometimes dropped it; so did grabbing it again at another spot | The first hand's release went out at once as a drop, which the host accepted; the other hand's hold then reached the host as a change to an item the player no longer had, was refused, and the refusal's correction dropped it. A late acceptance of that drop could also take the item out of the hand that had it again | A gentle release (under 2 m/s relative to the player's car) is held back for 0.3 s and sent only if no hand has the item by then; a re-grab inside that is just a hand change (`NetworkedItem.DeferLocalRelease`, `ItemReleaseGrace`). Past it, the host's acceptance of the drop doesn't undo a re-grab (`ItemSnapshotApplicationPolicy.IsSupersededLocalRelease`), the player who let go counts as having the item for the pickup, and a pickup made before an unrelated object-state change is still taken (below). `item-release` scenario, `MODE=regrab` and `regrab-late` | Code fixed, untested |
| A thrown lantern lost its momentum and snapped back to where it was let go; other players saw it jitter; one thrown from outside onto a train vanished | The thrower's game sent the lantern's flame and wick as it flew; it no longer held the item, so the host refused them, and the correction (with the position) put the item back at the release point. Every other game did the same with its own copy, and each correction set it off again. Every object-state change the host took (its own copy's flame among them) also advanced the revision, which voided the thrower's physics lease: its landing, on a car or not, was refused, and the record stayed at the release point (found reading the code, not seen live) | Only the holder's game sends an item's object state (book pages aside); the host ignores, without a correction, any it gets from a non-holder (`ItemObjectStateAuthority`). The record keeps the run of revisions that changed only object state, and transitions and physics leases from inside it stay good (`ItemPlacementRevision`), so the thrower's settlement, car parent included, commits where the item came to rest. `item-release`, `MODE=throw-flatcar` and `object-state` | Code fixed, untested |
| Lighting or turning up a lantern another player had set down reached no one else (Teflo's at 0.8, the server's and Gear's unlit) | The knob change came from a player who didn't hold the lantern. The host refused it with a correction, and with tonight's live patch ignored it: either way the host never learned it. A value changing alone can't tell a player's action from a copy drifting, which other games were sending several times a second | A player working an item where it lies opens a 1.5 s window by their own action: grabbing or using one of its controls (a lantern's knob, a mounted gadget's switch), or lighting it with a flame they hold. Changes inside it go out marked as an interaction, and the host takes a marked change from a player who has the item within 6 m of it, as object state only (`ItemInteractionPatch`, `ItemObjectStateAuthority.HostAcceptsWorldInteraction`). A received wick also sets the knob. `item-release`, `MODE=interact` | Code fixed, untested |
| The server crashed at 01:08 after 2 h 42 min: Mono's garbage collector aborted (0x80000003 in `mono-2.0-bdwgc`) inside an allocation. The log was 92 MB | Not proven. The allocation was a `Client_OnCoalPileInteraction` debug line. Every CCL mechanical-stoker tender (S464, 2901, S484) logged two of these per frame all night: 340,000 lines. "Got invalid NetId" ×3 per frame and a 24 KB `SeasonStatePacket` compressed several times a second also allocated steadily. The likely cause is heap growth over the session. The prank loco's 500 km/h run, loading the world quickly, may have tipped it over. The coal-pile lines were real sends: the tender's coal pile was reported by watching its consume port, which the stoker writes every tick, so the host's own client sent itself two reliable packets a tick per stoker tender (the server ignores them), and a client's stoker would have been taken on top of the host's. The invalid NetIds (600, 1125, 1126) were three FEF-3s (L-069) deleted with items settled in them: the host's train-item wake manager samples every car with settled items each frame and kept them, since DV pools a deleted car with what's in it and the items were never removed. `SeasonStatePacket` is DVSeasons' own heartbeat (`SeasonRuntime.Publish`, every 5 s, or sooner for urgent drift changes): about 110 KB in five 24 KB chunks, each compressed once and sent to all. It already leaves out unchanged sub-snapshots, so what's left changes between beats; that's DVSeasons' code to change, not ours. (Most of the rest, `Client_OnFireboxAddCoal` and "Sending CommonItemUpdatePacket", is already gone from this branch) | Report only `CoalPileSimController.TryRemoveCoal`/`TryAddCoal` (the shovel), from clients only (`CoalPileTransferCoalPatch`); the wake manager drops a car that no longer exists; "Got invalid NetId" and the per-packet "Compressed N to M bytes" are removed (16dccc38). The `stoker-coal` scenario now counts TenderCoal sends | Fixed, untested |

Other code also uses the per-session player number as an owner (Lost and Found's owner distance, for
one), so a saved record whose owner hasn't joined yet can point at whoever has that number now. Clear
the number when a record is restored, and set it when its owner logs in.

**Merged after the session (2026-10-18), not yet run in a game:** the side branches with the
session's other fixes are merged into `dev/player-state`: player-driven controls only
(`claude/focused-haibt-37d703`), VR belt items (`fix/belt-items`), items after they leave the hand
(`fix/item-release-sync`), the regression scenarios (`claude/determined-mahavira-831b1d`) and the
server app (`dev/server-app`). Each merge builds and the core tests pass (356). Still to do before
the next release:

1. A Debug build on one test pair: run the new scenarios (`settle-window`, `compressor-signal`,
   `stoker-coal`, `overview-pickup`, `remote-held-item`, `owned-items`, the five `item-release`
   ones) and `cab-controls` with `LOCOS="LocoS282A LocoDE6"`.
2. A VR check by a player: belt items, hand swaps and re-grabs, throws.
3. The rerail fix (`fix/rerail-follower`: code, scenario and docs done, not run in a game): merge it,
   and run `rerail` and `rerail-host` on that test pair.
4. Build Release. Every player installs the new zip.

## Session 3: 2026-10-19, dedicated server, 3 players

**Setup:** release 2026.10.19.1 on a dedicated host on the owner's PC; the owner joined from VR on
the same PC.

| What happened | Cause | Fix | Status |
|---|---|---|---|
| A mug taken off the VR belt and clipped straight back fell to the floor at once, and could never be picked up again | Read live from the owner's game: the mug was in belt slot 33 of DV's inventory, active and unsnapped on the floor, and DV refuses to grab an item its inventory says is stashed. Hooks on the snap points showed what unsnapped it: taking it off the belt reserves slot 33; letting go clears the reservation, and that claim change sent `InHand` in the release frame; the belt took the mug at the end of the frame and `InInventory` went out; then the host's echo of that `InHand` arrived and `PrepareForStateChange` unsnapped the mug, leaving it in the slot | The held VR release frame sends nothing (the item stays dirty for the next frame), and the owner's own `InHand` echo is ignored once the item is ungrabbed and in their inventory (`BeltItemPolicy.IsSupersededOwnHold`; "VR belt items" in `docs/design/item-world-sync.md`) | Fixed, tested (the same logic as runtime patches in the owner's VR game: every clip-back stayed, and nothing got stuck again) |
| Even with that fixed, a clip-back sometimes still drops the item (it can be picked up) | DV's own belt decision, logged at release: in the drops, the hand had left the slot's trigger, or was inside it but not touching the slot's pointer detector. The mod plays no part between the grab and the release | None: release once the slot highlights | Not a bug |
