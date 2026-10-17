// Host: a point on open line, 4-8 km from the host's body and over 400 m from any station, so the
// ground beside the track is terrain rather than platforms and yards. Result: its absolute x and z,
// and how far it is from the host's body.
var body = PlayerManager.PlayerTransform.position;
var stations = StationController.allStations.Select(s => s.transform.position).ToList();
var rnd = new System.Random(7);
var point = UnityEngine.Object.FindObjectsOfType<RailTrack>()
    .Select(t => (Vector3)t.curve.GetPointAt(0.5f))
    .Where(p => { var d = Vector3.Distance(p, body); return d > 4000 && d < 8000; })
    .Where(p => stations.All(s => Vector3.Distance(s, p) > 400f))
    .OrderBy(_ => rnd.Next())
    .First();
var a = Dev.Abs(point);
return new { x = a.x, z = a.z, d = Math.Round(Vector3.Distance(point, body)) };
