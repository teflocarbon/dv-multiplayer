// Host: what's under the client's player here (the server should have terrain there however far
// it is from the host's own body), and whether its Far scenery tile is loaded there. Result: the top
// collider's name, the terrain collider's name, and whether the Far tile is loaded.
var lifecycle = NetworkLifecycle.Instance;
var player = Multiplayer.Server.Session.NetworkServer.Instance.ServerPlayers.First(p => p != Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer);
var hits = Physics.RaycastAll(player.WorldPosition + Vector3.up * 30f, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore)
    .Where(h => h.collider.attachedRigidbody == null).OrderBy(h => h.distance).ToList();
var terrains = hits.Where(h => h.collider is TerrainCollider).Select(h => h.collider.name).ToList();
var far = WorldStreamingInit.Instance.farScenesStreamer;
return new { farLoaded = far != null && far.IsSceneLoaded(player.WorldPosition), top = hits.Count > 0 ? hits[0].collider.name : null, terrain = terrains.FirstOrDefault(), fromHostBody = Math.Round(Vector3.Distance(player.WorldPosition, PlayerManager.PlayerTransform.position)) };
