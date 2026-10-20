// Host: run at about 2 FPS for a while (each frame stalls), as a host that hitches badly.
// Args: seconds (real time), frameMs (default 480). Result: frames, game and real seconds, and
// when the stall began and ended (startUtc, endUtc: ms since the Unix epoch).
float seconds = float.Parse(ctx.Args["seconds"]);
int frameMs = ctx.Args.TryGetValue("frameMs", out var f) ? int.Parse(f) : 480;
long startUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
float t0 = Time.realtimeSinceStartup, g0 = Time.time; int frames = 0;
while (Time.realtimeSinceStartup - t0 < seconds) { System.Threading.Thread.Sleep(frameMs); frames++; yield return null; }
ctx.Result = new { frames, gameSeconds = Math.Round(Time.time - g0, 1), realSeconds = Math.Round(Time.realtimeSinceStartup - t0, 1),
    startUtc, endUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
