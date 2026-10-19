// Either player: pick up a job's overview, let go of it above the office bin by its station's
// validator (vanilla's JobAbandoner) and let it fall in, as a player throwing it away does. Waits
// for the paper to be taken away (re-rolled) or thrown back out. Args: job (the job's NetId).
var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n != null && n.NetId.ToString() == ctx.Args["job"]);
var item = nj.JobOverview ?? throw new Exception("no overview for " + nj.Job.ID);
ushort netId = item.NetId;
var validator = nj.Station.JobValidator ?? throw new Exception("no validator at " + nj.Station.name);
var bin = UnityEngine.Object.FindObjectsOfType<JobAbandoner>()
    .OrderBy(b => Vector3.Distance(b.transform.position, validator.transform.position)).First();
var trigger = bin.GetComponent<Collider>();
Bounds bounds = trigger.bounds;
Vector3 above = bounds.center + Vector3.up * (bounds.extents.y + 0.25f);

// (Already in hand when the player picked it up before boarding a car.)
if (Hands.Held == null || Hands.Held.NetId != netId)
{
    if (Hands.Held != null) yield return Hands.Drop();
    yield return Hands.Pickup(netId);
}
for (int i = 0; i < 10; i++) yield return null;
yield return Hands.Drop();
Hands.ReleaseControl();
// Let go on a car, the paper is the car's (DV parents it to the car the player stands on); one
// that really flew to the bin would have left the car on the way. Put it in the world.
item.ParentToWorld();
var body = item.Item.ItemRigidbody;
body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
body.position = above; item.transform.position = above;

// It drops in; then it's either destroyed with its job or thrown up and out.
// (Applying the throw deactivates the paper for a frame: only a destroyed one is gone.)
float highest = 0f; bool wentIn = false;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 8f;)
{
    if (item == null) break;
    if (bounds.Contains(item.transform.position)) wentIn = true;
    if (wentIn) highest = Mathf.Max(highest, item.transform.position.y - (bounds.center.y + bounds.extents.y));
    yield return null;
}
bool gone = item == null;
Vector3 binTop = bounds.center + Vector3.up * bounds.extents.y;
ctx.Result = new
{
    gone, wentIn, overview = netId,
    // how far above the bin's top the paper flew after going in
    flewUp = Math.Round(highest, 2),
    fromBin = gone ? -1 : Math.Round(Vector3.Distance(new Vector3(item.transform.position.x, 0, item.transform.position.z), new Vector3(binTop.x, 0, binTop.z)), 2),
    inBin = !gone && bounds.Contains(item.transform.position),
    bin = Dev.Abs(bin.transform.position).ToString("F1"), binSize = bounds.size.ToString("F2")
};
