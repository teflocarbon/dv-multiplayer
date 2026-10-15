# Changelog

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
