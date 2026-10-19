# Changelog

## 2026.10.19.3 (beta)

A playtest build: one fix, unlimited flashlight batteries, and logging to explain the next "train came apart". Every player and the server need this version. None of it has been tested in a game yet.

### VR
- Telekinesis on levers and dials keeps working after you step from a loco into its tender while holding one. It used to stay broken until a restart.

### Items
- Flashlights have unlimited battery, and a flat one is topped back up to full. Other items' batteries drain as before.

### Logging
- The host logs every coupler command it refuses (with the reason, and for "out of range" the distance it measured) and every uncouple.
- Uncouples, derails, removed cars and every item move, change of hands or removal record why they happened and who did it, on the host and on clients.
- With the dev tools mod installed, the debug system runs in release builds too.

## 2026.10.19.2 (beta)

Fixes from the third playtest, the first on a dedicated server with three players. Every player and the server need this version. None of these fixes has been tested in a game yet.

### Trains
- Train controls respond the moment you move them on a client: the whistle, horn, bells, dynamo and sanders follow your hand instead of waiting a round trip for the host. Other players' controls, doors, windows and levers play out smoothly instead of in steps.
- A handcar moves when a client pumps it, and its crank turns smoothly. Doors, windows, hatches and the other controls on a car's body take turns between players the same way cab controls do.
- A handcar's handbrake stays on while someone holds it, instead of the other games springing it off.
- A speedometer no longer flicks between two needles on clients.
- A client's train no longer surges 5-15 km/h every second when the host's frame rate hitches.

### Items
- In VR, an item taken off the belt and clipped straight back stays on the belt instead of falling to the floor and getting stuck.
- A flashlight no longer falls off the belt or flickers other lights, and its switch and battery belong to whoever carries it.
- In VR, an item another player is holding no longer falls out of their hand in your game.
- A VR throw keeps its momentum on a slow connection instead of stopping dead.
- Each of your radio, maps and other essentials keeps a slot of its own, so a lost one can always be recalled.
- Lost and Found no longer lists items that were destroyed, such as booklets of finished jobs.
- Shop scanners and office props no longer vanish for a player who joined while a taken prop was waiting to come back.

### Other
- On a dedicated server, an admin can pay the fees from the fees screen, and paying them uses insurance as the fees screen does.
- Cargo explosions get quieter with distance instead of sounding as loud everywhere in the world.

## 2026.10.19.1 (beta)

Dedicated servers, plus the fixes from the first two playtests on one. Covers everything since 0.1.16.15, including the playtest builds 2026.10.17.1 and 2026.10.17.2. Every player needs this version: a game on a different version won't let you join. Most of these fixes haven't been tested in a game yet.

### Dedicated server
- A game can host with nobody playing it. It pauses when everyone has left, saves on a timer, and has admins.
- **DV Multiplayer Server.exe** sets up and runs the server: the save, server settings, admins, which mods to load, the log and commands. The server runs from its own folder with its own mods and settings. Each start refreshes them from your game's copy.
- A watchdog restarts the server if it crashes and keeps backups of the save.
- The world loads around players and derailed cars, not around the server's own parked player.
- Admin commands: `/give`, `/restore`, `/repair`, `/weather`, `/time` and `/season` (with the Dynamic Seasons mod).
- Fixed a server crash after a few hours, caused by per-frame coal-pile packets.

### Players and joining
- Players are their Steam accounts, checked when they log in. Old careers are converted once.
- The owner is no longer kicked whenever someone else joins or leaves.
- Clients no longer get a fresh set of starting items on every join.

### Items
- Your radio and maps keep their inventory slots instead of turning up lying in the world.
- Anything in your inventory or hand is saved, even items you found in the world.
- Items left in a car that gets deleted go to their owner's Lost and Found.
- Job overviews another player could pick up no longer jump back to the table for you.
- In VR, items no longer vanish from your hand when you light a lantern, swap hands or re-grab them. Items clipped to the belt stay on the belt.
- Thrown items keep their momentum and land in the same place for everyone, including on trains.
- You can light or turn up a lantern someone else set down.

### Trains
- S282 compressor, dynamo, cylinder cocks, lubricator and oiling work from a client. So does a DE6's window.
- Oiling caps no longer reopen on their own.
- A DE6's compressor builds air again, so its brakes work.
- Stoker locos (S484, 2901) no longer overfill the firebox.
- A rerailed loco stays put on clients instead of sliding and flinging.
- Derailed cars far from the host stay where the host has them instead of falling through the ground.
- A leaking tank car leaks from the same place for everyone.
- Ground fire goes out once its fuel is gone. Vanilla leaves it burning forever.

### VR
- Fixed the frame rate collapsing over a session. Hand colliders were piling up.

### Known issues
- Friends-only servers don't appear in the server browser. Use a public server with a password.
- The whistle can stick.
- A client's wallet can show a few dollars off on very large balances (display only).
- The fees screen doesn't work from a client. Use `/payfees`.

## 0.1.16.15 (beta)

Mostly trains. Every player needs this version: a game on a different version won't let you join.

### Trains
- Stopped trains settle properly for everyone, instead of drifting after a bump or jiggling in yards.
- After a pile-up, crashed cars come to rest in the same places for everyone, even with lag. Derailed cars end up lying the same way for everyone.
- Cars that end up coupled on your screen but not the host's are uncoupled within about a second, instead of being thrown around.
- Couplings, hoses and cocks no longer stop syncing partway through a session. It happened when a car was deleted and another one of the same type spawned at the same moment, for example when job trains came and went.

### Joining and connection
- A host whose game has a broken empty train set (left behind by cars that crashed inside each other) no longer refuses everyone who tries to join.

### Known issues
- Gadget sync is partial: holes and switches are synced, but removing gadgets isn't finished.
- Other players can disappear from view in the F3 camera.
- A fast train can occasionally snap a few metres on a client.

## 0.1.16.14 (beta)

A big one. The host now has the final say on items, trains, shops, sleep and most of the world, so everyone in a session sees the same game. Every player needs this version: a game on a different version won't let you join.

### Items and inventory
- Items no longer duplicate, vanish when someone disconnects, or rain from the sky when a save loads.
- Your inventory and the item in your hand are saved with the host's game and come back when you rejoin.
- Personal items go to whoever picks them up. Nobody leaves the game with something they borrowed.
- Items you throw or drop show up in the same place for everyone. Items that fall through the map are rescued.
- Items in a loco cab ride along with the train instead of sliding off or getting left behind.
- Items in storage containers are kept by the host, so everyone sees the same contents.
- Lost and Found works in multiplayer, and you can get your essential items back.
- Flashlights, lanterns, shovels and books stay in sync, and flashlights no longer drain early.
- Banknotes show the right amount, and money a client picks up goes into the shared wallet.
- You get the starter kit on your first join only, not every time you join.

### Trains
- Coupling and uncoupling, brake hoses, cocks and MU cables are handled by the host, so a train is coupled the same way for everyone. This works from the menu, a loco cab, the remote and the in-cab decoupler.
- Other players' trains move smoothly instead of snapping into place.
- Trains no longer snap back after the host lags.
- Uncoupling or breaking a coupler at speed no longer dumps the brakes on one player's screen.
- Collisions show up straight away for everyone.
- Players with low frame rates stay in sync.
- A car derailed on your screen only is put back on the track.
- A car that never stops moving (for example one stuck in the ground) can be rerailed after 30 seconds.
- Newly spawned cars are no longer flung across the map.
- Deleting a car no longer deletes the players standing on it.
- Paying off a loco's debt no longer stops trains that are moving or have someone aboard.
- Clients can refuel at service stations again.

### Explosions and hazards
- Explosions are synced. Everyone sees and hears them, and the host decides what happens to the trains.
- Hazmat spills, fires and corrosion on the ground are synced, including for players who join later.

### Jobs
- When someone takes a job, its paperwork disappears from everyone else's inventory.
- A failed job validation no longer deletes the job.
- Job booklets and reports are synced. This is still a work in progress.

### Shops and money
- Clients can buy from shops. You pay into the shared wallet, your change comes back, and what you buy belongs to you.
- Shop stock is the same for everyone.

### World
- Everyone shares one sleep cooldown, so you can't skip time by taking turns in bed.
- Difficulty settings are applied properly, including for players who join while the host is still loading.
- The comms radio's sandbox cargo loader works for clients.
- Arriving at a station causes a much smaller stutter than before.

### Joining and connection
- Clients are no longer disconnected from career saves, and joining no longer hangs.
- Rejoining after a crash works.
- Large joins over Steam are more reliable.
- Other players stay in sync when packets are lost or they're riding a train.
- Only public games are listed in the server browser.

### Interface
- Pressing Esc on the disconnect popup no longer disconnects you.
- Game keybinds are disabled while you're typing in chat.
- High-ping warnings appear less often.
- The password keyboard works in VR.
- More French translations.
- Works with SkinManager.

### Known issues
- Gadget sync is partial: holes and switches are synced, but removing gadgets isn't finished.
- Other players can disappear from view in the F3 camera.
- A fast train can occasionally snap a few metres on a client.
- A "bounce" while coupling has been reported but not reproduced yet. If you see it, tell us what you were doing.

Includes fixes from the main Multiplayer mod.
