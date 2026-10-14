// Record frame times for a while (args: seconds); report the worst frames and the total time
// spent in frames over 100 ms (stalls a player would notice).
float seconds = float.Parse(ctx.Args["seconds"]);
var frames = new List<(float at, float ms)>();
float t0 = Time.realtimeSinceStartup, last = t0;
while (Time.realtimeSinceStartup - t0 < seconds)
{
    yield return null;
    float now = Time.realtimeSinceStartup;
    frames.Add((now - t0, (now - last) * 1000f));
    last = now;
}
var worst = frames.OrderByDescending(f => f.ms).Take(5).Select(f => $"{f.ms:F0}ms@{f.at:F1}s").ToList();
ctx.Result = new
{
    frames = frames.Count,
    worstMs = Math.Round(frames.Max(f => f.ms)),
    stallsOver100ms = frames.Count(f => f.ms > 100f),
    stalledSeconds = Math.Round(frames.Where(f => f.ms > 100f).Sum(f => f.ms) / 1000f, 2),
    worst
};
