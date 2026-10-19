// Host: pick the item up and throw it, then watch it until it settles: the host's own copy is
// simulated here under a spatial lease and committed when it comes to rest. Records every
// revision the host's copy shows, so a revision that goes back (a stale echo applied) shows up.
// Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (int i = 0; i < 120 && !NetworkedItem.TryGet(id, out item); i++) yield return null;
if (item == null) throw new Exception("item " + id + " never arrived");
if (Hands.Held != null) yield return Hands.Drop();
yield return LocalPlayer.GoTo(item);
yield return Hands.Pickup(id);
for (int i = 0; i < 20; i++) yield return null;
uint heldRevision = item.AuthorityRevision;
Vector3 dir = (PlayerManager.PlayerTransform.forward + Vector3.up * 0.5f).normalized;
yield return Hands.Throw(dir);
var manager = NetworkedItemManager.Instance;
var revisions = new List<uint> { item.AuthorityRevision };
bool leaseSeen = false, settled = false;
int wentBack = 0;
float restSince = -1f, t0 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - t0 < 15f)
{
    yield return null;
    if (item == null) throw new Exception("item " + id + " was destroyed in flight");
    uint revision = item.AuthorityRevision;
    if (revision < revisions[revisions.Count - 1]) wentBack++;
    if (revision != revisions[revisions.Count - 1]) revisions.Add(revision);
    bool leased = manager.HasActiveSpatialState(id);
    leaseSeen |= leased;
    Rigidbody body = item.Item?.ItemRigidbody;
    bool resting = !leased && (body == null || body.IsSleeping() || body.isKinematic || body.velocity.sqrMagnitude < 0.0025f);
    if (!resting) { restSince = -1f; continue; }
    if (restSince < 0f) restSince = Time.realtimeSinceStartup;
    if (leaseSeen && Time.realtimeSinceStartup - restSince >= 1f) { settled = true; break; }
}
ctx.Result = new
{
    item = id,
    heldRevision,
    leaseSeen,
    settled,
    wentBack,
    revisions = string.Join(",", revisions),
    seconds = Time.realtimeSinceStartup - t0
};
