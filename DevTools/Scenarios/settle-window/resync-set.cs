// Client: asks the host for a full resync of the car, and as soon as it lands (the host's value
// for the port has just been received) sets the car-body control that feeds the port, with no
// grab: what a VR hand closing an oiling cap does. The change falls in the 1.5 s the client waits
// for the host's values to settle. Args: loco, port (a port id, or part of one: the first car-body
// control whose port contains it), delay (seconds after the resync, default 0.3), value (default:
// the other end from where it is). Returns the port, the value set, and the client's value 3 s on.
var car = Trains.Find(ctx.Args["loco"]);
var ntc = car.GetComponent<NetworkedTrainCar>();
// Car-body controls are on the external interactables, which vanilla instantiates under the car's
// interior transform, not under the car: wait for them to load (the player is on the car), then look
// there as well as on the car itself.
for (float t0 = Time.realtimeSinceStartup; !car.AreExternalInteractablesLoaded && Time.realtimeSinceStartup - t0 < 10f; )
    yield return null;
var feeders = new[] { car.transform, car.interior }.Where(root => root != null).Distinct()
    .SelectMany(root => root.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true)).Distinct()
    .Select(c => new { control = c, port = c.GetComponent<DV.Simulation.Ports.InteractablePortFeeder>()?.portId })
    .Where(x => !string.IsNullOrEmpty(x.port)).ToList();
var wanted = ctx.Args["port"];
var pick = feeders.FirstOrDefault(x => x.port == wanted) ?? feeders.FirstOrDefault(x => x.port.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
if (pick == null) throw new Exception("no car-body control feeds a port like '" + wanted + "'; car-body ports: " + string.Join(", ", feeders.Select(x => x.port).Distinct()));
if (!ntc.simulationFlow.TryGetPort(pick.port, out var port)) throw new Exception("no port " + pick.port);
uint portNetId = NetworkedTrainCar.GetPortNetId(pick.port);
float value = ctx.Args.TryGetValue("value", out var v) && !string.IsNullOrEmpty(v)
    ? float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)
    : port.Value < 0.5f ? 1f : 0f;
float delay = ctx.Args.TryGetValue("delay", out var d) && !string.IsNullOrEmpty(d) ? float.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : 0.3f;

float before = Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(ntc).hostPortReceivedAt.TryGetValue(portNetId, out float t) ? t : 0f;
NetworkLifecycle.Instance.Client.SendTrainSyncRequest(ntc.NetId);
float asked = Time.realtimeSinceStartup;
while ((!Multiplayer.Client.Trains.NetworkedTrainCarClient.Of(ntc).hostPortReceivedAt.TryGetValue(portNetId, out t) || t <= before) && Time.realtimeSinceStartup - asked < 10f)
    yield return null;
if (t <= before) throw new Exception("the resync never arrived");
yield return new WaitForSecondsRealtime(delay);
float since = Time.realtimeSinceStartup - t;
pick.control.SetValue(value);
yield return new WaitForSecondsRealtime(3f);
ctx.Result = new { port = pick.port, value, since = Math.Round(since, 2), client = Math.Round(port.Value, 3) };
