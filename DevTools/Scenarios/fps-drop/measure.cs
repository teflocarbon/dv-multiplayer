// Client, riding the loco: along-track error against the host's train (projected to now) and
// the client's speed, at normal frame rate, during a frame-rate drop, and after it.
// Args: loco, fps (the dropped frame rate), seconds (per phase).
var loco = Trains.Find(ctx.Args["loco"]);
var nb = loco.Bogies[0].GetComponent<Multiplayer.Shared.Trains.NetworkedBogie>()
         ?? throw new Exception("no NetworkedBogie on " + loco.ID);
int normal = Application.targetFrameRate;
float phase = float.Parse(ctx.Args["seconds"]);
var phases = new List<object>();
foreach (var (name, fps) in new[] { ("before", normal), ("drop", int.Parse(ctx.Args["fps"])), ("after", normal) })
{
    Application.targetFrameRate = fps;
    float worst = 0f, sum = 0f; int n = 0; float t0 = Time.realtimeSinceStartup, g0 = Time.time;
    while (Time.realtimeSinceStartup - t0 < phase)
    {
        yield return null;
        if (nb.TryGetAlongTrackError(loco, out float e)) { worst = Mathf.Max(worst, Mathf.Abs(e)); sum += Mathf.Abs(e); n++; }
    }
    phases.Add(new { name, fps, worst = Math.Round(worst, 2), mean = n > 0 ? Math.Round(sum / n, 2) : -1, gameTimeRatio = Math.Round((Time.time - g0) / (Time.realtimeSinceStartup - t0), 2), speed = Math.Round(Mathf.Abs(loco.GetForwardSpeed()), 1) });
}
Application.targetFrameRate = normal;
ctx.Result = new { phases };
