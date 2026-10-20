// Either peer: watches a service station's plug every 0.1 s for `seconds` (args plug, seconds).
// Coroutine; result: how far it got from its home socket (maxAway, m), whether the yank's settle
// was seen (vanilla's yankOutOfHand cleared while it lands), and at the end where it is (absolute
// x/z), how far from home, whether it is plugged in at home, and the server's holder.
NetworkedPluggableObject.Get(ushort.Parse(ctx.Args["plug"]), out NetworkedPluggableObject netPlug);
float seconds = float.Parse(ctx.Args["seconds"], System.Globalization.CultureInfo.InvariantCulture);
if (netPlug == null || netPlug.PluggableObject == null || netPlug.PluggableObject.startAttachedTo == null)
{
    ctx.Result = new { found = false };
    yield break;
}
var plug = netPlug.PluggableObject;
Func<float> away = () => Vector3.Distance(plug.transform.position, plug.GetSnappedWorldPositionFor(plug.startAttachedTo));
float maxAway = 0f;
bool settleSeen = false;
float t0 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - t0 < seconds)
{
    maxAway = Math.Max(maxAway, away());
    settleSeen |= !plug.yankOutOfHand;
    yield return new WaitForSecondsRealtime(0.1f);
}
var abs = Dev.Abs(plug.transform.position);
ctx.Result = new
{
    found = true,
    maxAway = Math.Round(maxAway, 2),
    settleSeen,
    away = Math.Round(away(), 2),
    atHome = plug.State == PluggableObject.PluggableState.PluggedIn && plug.Socket == plug.startAttachedTo,
    x = abs.x, z = abs.z,
    heldBy = (int)netPlug.HeldByPlayerId,
    // how far from the hose origin a held plug is yanked
    limit = netPlug.Hose == null ? -1 : Math.Round(netPlug.Hose.rope.ropeParams.ropeLength * netPlug.Hose.maxLengthMultiplier, 2),
};
