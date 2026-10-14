// Host: the longest empty track near the players. Args: need (metres, default 300), range (default 400).
float need = ctx.Args.TryGetValue("need", out var n) ? float.Parse(n) : 300f;
float range = ctx.Args.TryGetValue("range", out var r) ? float.Parse(r) : 400f;
var me = PlayerManager.PlayerTransform.position;
var cars = UnityEngine.Object.FindObjectsOfType<TrainCar>();
var found = UnityEngine.Object.FindObjectsOfType<RailTrack>()
    .Where(t => t.curve != null && t.curve.length >= need)
    .Select(t => new { t, d = Vector3.Distance(me, t.curve.GetPointAt(0.5f)) })
    .Where(x => x.d < range)
    .Where(x => !cars.Any(c => c.Bogies != null && c.Bogies.Any(b => b != null && b.track == x.t)))
    .OrderBy(x => x.d).Take(5)
    .Select(x => new { x.t.name, length = Math.Round(x.t.curve.length), distance = Math.Round(x.d) }).ToArray();
return found;
