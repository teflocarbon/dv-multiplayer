// Client: reloads the loco's cab interior, as walking out of and back into its load distance does:
// first `quick` times in a row with the cab unloaded again 0.3 s after each load (its hook pass
// then still running), then once more after leaving it unloaded `gap` seconds. `after` seconds
// after the last load it grabs a lever, as a player reaching for one right after the cab appears
// does, checks it took the lever's lease, and lets go.
// Before 2026-10-20 a cab with a control the old ready check never passed (an overrider with no
// port and no feeder: the S282's ReleaseCyl) hooked nothing for 2 s, so that grab took no lease; a
// cab unloaded within those 2 s threw in HookControls; and one left unloaded 2 s logged "failed
// to load an interior". `portless` says whether this cab has such a control.
var car = Trains.Find(ctx.Args["loco"]);
var client = Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>());
var inv = System.Globalization.CultureInfo.InvariantCulture;
float after = float.Parse(ctx.Args["after"], inv), gap = float.Parse(ctx.Args["gap"], inv);
int quick = int.Parse(ctx.Args["quick"]);
IEnumerator Load()
{
    // The game's own load distance check may have loaded it again already.
    for (int i = 0; i < 3 && car.loadedInterior == null; i++)
        yield return null;
    if (car.loadedInterior == null)
        car.LoadInterior();
    if (car.loadedInterior == null)
        throw new Exception("the cab of " + car.ID + " didn't load again");
}
for (int i = 0; i < quick; i++)
{
    car.UnloadInterior();
    yield return Load();
    yield return new WaitForSecondsRealtime(0.3f);
}
car.UnloadInterior();
if (gap > 0)
{
    // Kept unloaded: the game would load it again at once with the player this close.
    car.blockInteriorLoading = true;
    yield return new WaitForSecondsRealtime(gap);
    car.blockInteriorLoading = false;
}
yield return Load();

string PortOf(DV.HUD.InteriorControlsManager.ControlReference r) =>
    !string.IsNullOrEmpty(r.overridableBaseControl?.portId) ? r.overridableBaseControl.portId
    : r.controlImplBase?.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId;
var manager = car.loadedInterior.GetComponent<DV.HUD.InteriorControlsManager>();
// Wait for the cab to make its controls (a frame or two), as the mod does.
for (int i = 0; i < 10 && manager != null && !manager.Initialized; i++) yield return null;
var refs = manager != null ? manager.controls.Values.ToList() : new List<DV.HUD.InteriorControlsManager.ControlReference>();
bool portless = refs.Any(r => r.overridableBaseControl != null && string.IsNullOrEmpty(PortOf(r)));
// A lever or a knob that feeds a port: taking hold of it asks for its lease.
var lever = refs.Where(r => r.controlImplBase is DV.CabControls.LeverBase or DV.CabControls.RotaryBase && !string.IsNullOrEmpty(PortOf(r)))
    .Select(r => r.controlImplBase).FirstOrDefault()
    ?? throw new Exception("no lever with a port in the cab of " + car.ID);
uint netId = Multiplayer.Shared.Trains.NetworkedTrainCar.GetPortNetId(PortOf(refs.First(r => r.controlImplBase == lever)));
float sinceLoad = 0;
while (sinceLoad < after) { yield return null; sinceLoad += Time.unscaledDeltaTime; }
bool hooked = client.controlToPortNetId.ContainsKey(lever);
lever.FireGrabbed();
yield return new WaitForSecondsRealtime(0.5f);
bool owned = client.locallyOwnedPortAuthorities.Contains(netId);
lever.FireUngrabbed();
// A sprung control is let go once it has settled (polled every 0.5 s), then the lease goes.
yield return new WaitForSecondsRealtime(3f);
ctx.Result = new { lever = lever.name, portless, hooked, owned, released = !client.locallyOwnedPortAuthorities.Contains(netId) };
