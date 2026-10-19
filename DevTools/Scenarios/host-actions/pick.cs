// Acting peer: the junction and the turntable nearest its player, by NetId (both games number them
// the same way), and whether fast travel may skip time.
var p = Dev.PlayerAbs.Value;
var j = NetworkedJunction.IndexedJunctions.Where(n => n != null && n.Junction != null)
    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), p)).First();
var t = NetworkedTurntable.IndexedTurntables.Where(n => n != null && n.TurntableRailTrack != null)
    .OrderBy(n => Vector3.Distance(Dev.Abs(n.transform.position), p)).FirstOrDefault();
return new
{
    junction = j.NetId,
    junctionDistance = Mathf.Round(Vector3.Distance(Dev.Abs(j.transform.position), p)),
    turntable = t == null ? 0 : t.NetId,
    turntableDistance = t == null ? -1 : Mathf.Round(Vector3.Distance(Dev.Abs(t.transform.position), p)),
    fastTravelAdvancesTime = Multiplayer.Client.World.TimeAdvancePatch.FastTravelAdvancesTime,
};
