// Either peer, for SECONDS: keep doing things a player does - short hops, long trips (world
// streaming), boarding the test loco, riding it (the host pushes it), going home, standing still -
// while recording this player's own position and where it sees the other player's avatar, four
// times a second, stamped with wall-clock time (both games run on one machine).
// Args: seconds, seed, loco, homeX, homeZ.
float seconds = float.Parse(ctx.Args["seconds"]);
var rnd = new System.Random(int.Parse(ctx.Args["seed"]));
var loco = Trains.Find(ctx.Args["loco"]);
var home = new Vector2(float.Parse(ctx.Args["homeX"]), float.Parse(ctx.Args["homeZ"]));
bool host = Multiplayer.Shared.Session.NetworkLifecycle.Instance.IsHost();
var own = new List<float[]>(); var seen = new List<float[]>(); var log = new List<string>();
bool running = true;
long T() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
IEnumerator Sample()
{
    while (running)
    {
        var me = Dev.Abs(PlayerManager.PlayerTransform.position);
        own.Add(new float[] { T() % 10000000, me.x, me.y, me.z });
        var other = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().FirstOrDefault();
        if (other != null) { var p = Dev.Abs(other.transform.position); seen.Add(new float[] { T() % 10000000, p.x, p.y, p.z }); }
        yield return new WaitForSecondsRealtime(0.25f);
    }
}
ctx.StartCoroutine(Sample(), "sample");
IEnumerator Wait(float s) { for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < s;) yield return null; }
float start = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - start < seconds)
{
    int a = rnd.Next(host ? 6 : 5);
    float at = Time.realtimeSinceStartup - start;
    switch (a)
    {
        case 0:
            var me = Dev.Abs(PlayerManager.PlayerTransform.position);
            log.Add($"{at:F0}s hop");
            yield return LocalPlayer.TeleportAbs(me + new Vector3((float)rnd.NextDouble() * 12 - 6, 0, (float)rnd.NextDouble() * 12 - 6), 0.5f);
            break;
        case 1:
            float ang = (float)(rnd.NextDouble() * Math.PI * 2), dist = 300f + (float)rnd.NextDouble() * 1200f;
            log.Add($"{at:F0}s trip {dist:F0} m");
            yield return LocalPlayer.TeleportToGround(home.x + Mathf.Cos(ang) * dist, home.y + Mathf.Sin(ang) * dist);
            yield return Wait(3f);
            break;
        case 2:
            log.Add($"{at:F0}s board");
            yield return Trains.Board(loco);
            yield return Wait(4f);
            break;
        case 3:
            log.Add($"{at:F0}s home");
            yield return LocalPlayer.TeleportToGround(home.x + (host ? 4f : 0f), home.y);
            break;
        case 4:
            log.Add($"{at:F0}s idle");
            yield return Wait(4f);
            break;
        case 5:
            log.Add($"{at:F0}s push loco");
            yield return Trains.Push(loco, 6f, 4f, 2f);
            break;
    }
}
running = false;
ctx.Result = new { own, seen, log };
