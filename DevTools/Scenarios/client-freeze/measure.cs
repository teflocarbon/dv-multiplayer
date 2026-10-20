// Client, riding the loco: along-track error against the host's train (projected to now) before
// the client freezes its main thread, and every frame for a few seconds after, with the loco's
// biggest step beyond its speed once the catch-up's second is over (a later snap is a full sync's).
// Args: loco, freezeMs, seconds (after the freeze).
var loco = Trains.Find(ctx.Args["loco"]);
var nbs = loco.Bogies.Select(b => b.GetComponent<Multiplayer.Client.Trains.NetworkedBogie>()).ToArray();
if (nbs.Any(b => b == null)) throw new Exception("no NetworkedBogie on " + loco.ID);
float? Error()
{
    float sum = 0f; int n = 0;
    foreach (var nb in nbs)
        if (nb.TryGetAlongTrackError(loco, out float e, acrossJoint: true)) { sum += e; n++; }
    return n > 0 ? sum / n : (float?)null;
}

float before = 0f;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 2f;)
{
    yield return null;
    if (Error() is float e) before = Mathf.Max(before, Mathf.Abs(e));
}
float speedBefore = Mathf.Abs(loco.GetForwardSpeed());

System.Threading.Thread.Sleep(int.Parse(ctx.Args["freezeMs"]));
float tFreeze = Time.realtimeSinceStartup;

float firstError = float.NaN, worstLate = 0f, settledAt = -1f, worstStepLate = 0f;
Vector3 last = Dev.Abs(loco.transform.position);
float seconds = float.Parse(ctx.Args["seconds"]);
var errors = new List<object>();
while (Time.realtimeSinceStartup - tFreeze < seconds)
{
    yield return null;
    float since = Time.realtimeSinceStartup - tFreeze;
    Vector3 here = Dev.Abs(loco.transform.position);
    float step = Vector3.Distance(here, last) - Mathf.Abs(loco.GetForwardSpeed()) * Time.deltaTime;
    last = here;
    float? e = Error();
    if (e is float err)
    {
        if (float.IsNaN(firstError)) firstError = err;
        if (settledAt < 0f && Mathf.Abs(err) < 1f) settledAt = since;
        if (since > 1f) worstLate = Mathf.Max(worstLate, Mathf.Abs(err));
        if (errors.Count < 12) errors.Add(new { t = Math.Round(since, 2), e = Math.Round(err, 2) });
    }
    if (since > 1f) worstStepLate = Mathf.Max(worstStepLate, step);
}
ctx.Result = new
{
    before = Math.Round(before, 2),
    speed = Math.Round(speedBefore, 1),
    firstError = float.IsNaN(firstError) ? (double?)null : Math.Round(firstError, 2),
    settledAt = Math.Round(settledAt, 2),
    worstLate = Math.Round(worstLate, 2),
    worstStepLate = Math.Round(worstStepLate, 2),
    errors,
};
