// Either peer: a service station's lever and plug. Args: op, and (except for find) station, plug,
// resource (NetIds and ResourceType).
//   find (host): the plug nearest a point (args x, z; default the player) that belongs to a
//     station with a resource lever.
//   state: whether this game has the station and plug set up (Refreshed), who holds the plug (the
//     server's HeldBy, and the player id this game shows holding it), whether the lever is held by
//     another player here, and this game's own player id.
//   lever (value 0/1): the lever's handler, as its RotaryAmplitudeChecker raises it when pulled
//     (1) and let go (0).
//   grab, dock: the plug's handlers, as DV raises them when it's picked up and put back home.
string op = ctx.Args["op"];
if (op == "find")
{
    // near a point (abs x/z args), or the player
    var p = ctx.Args.TryGetValue("x", out string fx) ? new Vector3(float.Parse(fx), 0, float.Parse(ctx.Args["z"])) : Dev.PlayerAbs.Value;
    Func<Vector3, float> flat = v => new Vector2(v.x - p.x, v.z - p.z).magnitude;
    var plug = Dev.All<NetworkedPluggableObject>()
        .Where(n => n != null && n.Station != null && n.Station.leverStateLookup.Count > 0 && n.PluggableObject != null && n.PluggableObject.startAttachedTo != null)
        .OrderBy(n => flat(Dev.Abs(n.transform.position))).First();
    var abs = Dev.Abs(plug.transform.position);
    return new { plug = plug.NetId, station = plug.Station.NetId, resource = (int)plug.Station.leverStateLookup.Keys.First(), name = plug.Station.StationName, x = abs.x, z = abs.z };
}
NetworkedPitStopStation.Get(ushort.Parse(ctx.Args["station"]), out NetworkedPitStopStation station);
NetworkedPluggableObject.Get(ushort.Parse(ctx.Args["plug"]), out NetworkedPluggableObject netPlug);
var resource = (DV.ThingTypes.ResourceType)int.Parse(ctx.Args["resource"]);
if (station == null || netPlug == null)
    return new { found = false };
if (op == "lever")
    station.leverStateLookup[resource].leverHandler(int.Parse(ctx.Args["value"]));
else if (op == "grab")
    netPlug.OnGrabbed(null);
else if (op == "dock")
    netPlug.OnPluggedIn(netPlug.PluggableObject, netPlug.PluggableObject.startAttachedTo);
var lifecycle = Multiplayer.Shared.Session.NetworkLifecycle.Instance;
return new
{
    found = true,
    refreshed = station.Refreshed && netPlug.Refreshed,
    heldBy = (int)netPlug.HeldByPlayerId,
    holding = (int)netPlug.playerHolding,
    remoteGrabbed = station.isResourceRemoteGrabbedDict.TryGetValue(resource, out bool g) && g,
    me = (int)lifecycle.Client.PlayerId,
};
