// Acting peer: the junction and the turntable nearest its player, by NetId (both games number them
// the same way), where the turntable's controls are (abs x/z: the players go there, as a turntable
// only turns where the world is loaded), and whether fast travel may skip time.
var p = Dev.PlayerAbs.Value;
var j = NetworkedJunction.IndexedJunctions.Where(n => n != null && n.Junction != null)
    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), p)).First();
var t = NetworkedTurntable.IndexedTurntables.Where(n => n != null && n.TurntableRailTrack != null)
    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), p)).FirstOrDefault();
var controller = t == null ? null : Resources.FindObjectsOfTypeAll<TurntableController>().FirstOrDefault(c => c.turntable == t.TurntableRailTrack);
Vector3 controls = controller != null ? Dev.Abs(controller.transform.position) : t != null ? Dev.Abs(t.transform.position) : Vector3.zero;
return new
{
    junction = j.NetId,
    junctionDistance = Mathf.Round(Vector3.Distance(Dev.Abs(j.transform.position), p)),
    turntable = t == null ? 0 : t.NetId,
    turntableDistance = t == null ? -1 : Mathf.Round(Vector3.Distance(Dev.Abs(t.transform.position), p)),
    tx = controls.x,
    tz = controls.z,
    fastTravelAdvancesTime = Multiplayer.Client.World.TimeAdvancePatch.FastTravelAdvancesTime,
};
