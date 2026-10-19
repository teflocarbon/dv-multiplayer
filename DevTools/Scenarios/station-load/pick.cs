// Host: the station farthest from every player, by yard id, with its office position.
var players = new List<Vector3> { PlayerManager.PlayerTransform.position };
players.AddRange(UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().Select(p => p.transform.position));
var station = StationController.allStations
    .OrderByDescending(s => players.Min(p => Vector3.Distance(p, s.transform.position)))
    .Skip(int.Parse(ctx.Args.TryGetValue("skip", out var k) ? k : "0")).First();
var at = Dev.Abs(station.transform.position);
return new { id = station.stationInfo.YardID, x = at.x, z = at.z, km = Math.Round(players.Min(p => Vector3.Distance(p, station.transform.position)) / 1000f, 1) };
