// Client: watch the host's train while the run moves the player near and far. Per distance band
// (from the player to the train's nearest car: near, mid, far), report how often the host's
// samples of the loco arrived, the interval the host announced, gaps long enough for the car to be
// taken as lost, the worst single physics step beyond the car's own speed (a snap), the slowest
// second (a stop-and-snap shows as a stall), and the worst step in the first 8 s back near.
// The slowest second is in this game's own time: below 12 fps DV runs game time slower than real
// time (it caps a frame's delta), which slows every train on this game, near or far, and isn't the
// sync's doing. Each band reports that rate (gameRate) and the frame rate. A snap within 5 s of
// this game freezing (frames over 0.25 s, e.g. loading after a teleport), no bigger than the host's
// train could have run meanwhile, is the frozen game catching up, counted apart (freezeSnaps).
// Args: loco, cars (comma-separated), seconds, full and mid (band edges, metres), speed (m/s).
var loco = Trains.Find(ctx.Args["loco"]);
var cars = ctx.Args["cars"].Split((char)44).Select(id => Trains.Find(id)).ToList();
var inv = System.Globalization.CultureInfo.InvariantCulture;
float seconds = float.Parse(ctx.Args["seconds"], inv);
float full = float.Parse(ctx.Args["full"], inv);
float mid = float.Parse(ctx.Args["mid"], inv);
float speed = float.Parse(ctx.Args["speed"], inv);
if (!Multiplayer.Shared.Trains.NetworkedTrainCar.TryGetFromTrainCar(loco, out var net))
    throw new Exception("loco " + loco.ID + " isn't networked");
var queue = loco.GetComponent<Multiplayer.Client.Trains.TrainSpeedQueue>() ?? throw new Exception("no speed queue on " + loco.ID);
var bogie = loco.Bogies[0].GetComponent<Multiplayer.Client.Trains.NetworkedBogie>();

string BandOf(float d) => d <= full ? "near" : d <= mid ? "mid" : "far";
var bands = new Dictionary<string, Dictionary<string, double>>();
Dictionary<string, double> Stats(string b)
{
    if (!bands.TryGetValue(b, out var s))
        bands[b] = s = new Dictionary<string, double> { ["seconds"] = 0, ["samples"] = 0, ["intervalTicks"] = 0, ["staleGaps"] = 0,
            ["worstGap"] = 0, ["worstStep"] = 0, ["slowestSecond"] = 999, ["slowestRealSecond"] = 999, ["minDistance"] = 1e9,
            ["maxDistance"] = 0, ["gameSeconds"] = 0, ["realSeconds"] = 0, ["frames"] = 0, ["worstStepAt"] = 0,
            ["freezeSnaps"] = 0, ["worstFreezeSnap"] = 0 };
    return s;
}
string Follower()
{
    float e = 0f;
    string error = bogie != null && bogie.TryGetAlongTrackError(loco, out e) ? $"{e:F2} m" : "none";
    return $"following {queue.Following}, held {queue.Held}, interval {queue.SampleIntervalSeconds:F2} s, error {error}, " +
           $"v {loco.GetForwardSpeed():F1} m/s, frame {Time.unscaledDeltaTime * 1000f:F0} ms";
}
var notes = new List<string>();
// The last second of the follower, for what led up to a snap: steps with an along-track error,
// the largest error, steps following.
var recent = new Queue<(bool hasError, float error, bool following)>();
string Before()
{
    int withError = recent.Count(r => r.hasError), following = recent.Count(r => r.following);
    float worst = recent.Where(r => r.hasError).Select(r => Mathf.Abs(r.error)).DefaultIfEmpty(0f).Max();
    return $"in the second before: error known {withError}/{recent.Count} steps, at most {worst:F2} m; following {following}/{recent.Count}";
}
float firstLeftNear = -1f;
// Long frames (over 0.25 s) in the last 6 s: how long this game was frozen.
var longFrames = new Queue<(float at, float length)>();
int lastLongFrame = -1;
string band = null, prevBand = null;
float bandSince = 0f, lastSampleAt = -1f, gapMaxFrame = 0f, approachWorst = 0f, prevNow = 0f, slowNotedAt = -99f;
int lastFrame = -1;
uint lastTick = net.lastTickProcessed;
Vector3 lastP = Dev.Abs(loco.transform.position);
var window = new Queue<(float t, float game, Vector3 p)>();
float t0 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - t0 < seconds)
{
    yield return new WaitForFixedUpdate();
    float now = Time.realtimeSinceStartup - t0;
    float dtReal = now - prevNow;
    prevNow = now;
    Vector3 me = Dev.Abs(PlayerManager.PlayerTransform.position);
    float distance = cars.Min(c => Vector3.Distance(Dev.Abs(c.transform.position), me));
    string b = BandOf(distance);
    if (b != band)
    {
        prevBand = band;
        if (band == "near" && firstLeftNear < 0f)
            firstLeftNear = now;
        band = b;
        bandSince = now;
        window.Clear();
        lastSampleAt = -1f;
    }
    var s = Stats(b);
    // The host hears of the player's new position a moment late: rates count from 3 s in.
    bool settled = now - bandSince > 3f;
    if (settled)
    {
        s["seconds"] += dtReal;
        s["realSeconds"] += dtReal;
        s["gameSeconds"] += Time.fixedDeltaTime;
        if (Time.frameCount != lastFrame)
            s["frames"]++;
    }
    lastFrame = Time.frameCount;
    s["minDistance"] = Math.Min(s["minDistance"], distance);
    s["maxDistance"] = Math.Max(s["maxDistance"], distance);
    gapMaxFrame = Mathf.Max(gapMaxFrame, Time.unscaledDeltaTime);
    if (Time.unscaledDeltaTime > 0.25f && Time.frameCount != lastLongFrame)
    {
        lastLongFrame = Time.frameCount;
        longFrames.Enqueue((now, Time.unscaledDeltaTime));
    }
    while (longFrames.Count > 0 && now - longFrames.Peek().at > 6f)
        longFrames.Dequeue();
    float frozen = longFrames.Sum(x => x.length);
    float lastFreezeAt = longFrames.Count > 0 ? longFrames.Last().at : -99f;

    // A new sample from the host (several in one frame count once: a lower bound).
    if (net.lastTickProcessed != lastTick)
    {
        lastTick = net.lastTickProcessed;
        if (settled)
        {
            s["samples"]++;
            s["intervalTicks"] = Math.Max(s["intervalTicks"], Math.Round(queue.SampleIntervalSeconds * 24f));
        }
        if (lastSampleAt >= 0f)
        {
            float gap = now - lastSampleAt;
            s["worstGap"] = Math.Max(s["worstGap"], Math.Round(gap, 2));
            // A gap with a frame hitch in it (a teleport loading the world) is the client's own, and
            // samples are seen once a frame: allow two frames on top of the client's own limit.
            if (gap > Multiplayer.Client.Trains.TrainSpeedQueue.StaleSeconds(queue.SampleIntervalSeconds) + 2f * gapMaxFrame && gapMaxFrame < 0.3f)
            {
                s["staleGaps"]++;
                if (notes.Count < 12) notes.Add($"{now:F1}s {b}: no sample for {gap:F2} s (interval {queue.SampleIntervalSeconds:F2} s)");
            }
        }
        lastSampleAt = now;
        gapMaxFrame = 0f;
    }

    // A step beyond what the car's own velocity moves it in one physics step is a snap.
    Vector3 p = Dev.Abs(loco.transform.position);
    float excess = Vector3.Distance(p, lastP) - loco.rb.velocity.magnitude * Time.fixedDeltaTime;
    lastP = p;
    bool afterFreeze = excess > 0.5f && now - lastFreezeAt < 5f && excess <= 2f * speed * (frozen + 1f) + 5f;
    if (afterFreeze)
    {
        s["freezeSnaps"]++;
        s["worstFreezeSnap"] = Math.Max(s["worstFreezeSnap"], Math.Round(excess, 2));
        if (notes.Count < 12)
            notes.Add($"{now:F1}s {b}: caught up {excess:F2} m, {now - lastFreezeAt:F1} s after this game froze for {frozen:F1} s ({Follower()})");
        excess = 0f;
    }
    if (excess > s["worstStep"])
    {
        s["worstStep"] = Math.Round(excess, 3);
        s["worstStepAt"] = Math.Round(now, 1);
        if (excess > 0.5f && notes.Count < 12)
            notes.Add($"{now:F1}s {b}: stepped {excess:F2} m beyond its speed ({Follower()}; {Before()})");
    }
    float knownError = 0f;
    bool known = bogie != null && bogie.TryGetAlongTrackError(loco, out knownError);
    recent.Enqueue((known, known ? knownError : 0f, queue.Following));
    while (recent.Count > 50)
        recent.Dequeue();
    if (b == "near" && (prevBand == "mid" || prevBand == "far") && now - bandSince < 8f)
        approachWorst = Mathf.Max(approachWorst, excess);

    // The slowest second of game time in the band, after its first 3 s (the client's own
    // teleport hitch), and the slowest real second.
    window.Enqueue((now, Time.fixedTime, p));
    while (window.Count > 0 && Time.fixedTime - window.Peek().game > 1f)
    {
        var first = window.Dequeue();
        if (first.t - bandSince <= 3f)
            continue;
        float d = Vector3.Distance(first.p, p);
        float gameSpeed = d / (Time.fixedTime - first.game);
        s["slowestSecond"] = Math.Min(s["slowestSecond"], Math.Round(gameSpeed, 2));
        if (now > first.t)
            s["slowestRealSecond"] = Math.Min(s["slowestRealSecond"], Math.Round(d / (now - first.t), 2));
        if (b != "near" && gameSpeed < 0.6f * speed && now - slowNotedAt > 2f && notes.Count < 12)
        {
            slowNotedAt = now;
            notes.Add($"{now:F1}s {b}: {gameSpeed:F1} m/s over the last game second ({Follower()})");
        }
    }
}
foreach (var s in bands.Values)
{
    s["samplesPerSecond"] = s["seconds"] > 0 ? Math.Round(s["samples"] / s["seconds"], 1) : 0;
    s["gameRate"] = s["realSeconds"] > 0 ? Math.Round(s["gameSeconds"] / s["realSeconds"], 2) : 0;
    s["fps"] = s["realSeconds"] > 0 ? Math.Round(s["frames"] / s["realSeconds"], 1) : 0;
    s["seconds"] = Math.Round(s["seconds"], 1);
    s["minDistance"] = Math.Round(s["minDistance"]);
    s["maxDistance"] = Math.Round(s["maxDistance"]);
}
ctx.Result = new { bands, approachWorst = Math.Round(approachWorst, 3), firstLeftNear = Math.Round(firstLeftNear, 1), notes };
