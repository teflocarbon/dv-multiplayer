// Client: pick up the job's booklet and hand it in at the nearest validator; report what printed.
// Args: job.
var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n.Job?.ID == ctx.Args["job"]);
var job = nj.Job;
var booklet = UnityEngine.Object.FindObjectsOfType<JobBooklet>().First(b => b.job == job);
var bookletItem = booklet.GetComponent<NetworkedItem>();
var validator = UnityEngine.Object.FindObjectsOfType<JobValidator>().OrderBy(v => Vector3.Distance(v.transform.position, PlayerManager.PlayerTransform.position)).First();
if (Hands.Held != null && Hands.Held.NetId != bookletItem.NetId) yield return Hands.Drop();
if (Hands.Held == null) yield return Hands.Pickup(bookletItem.NetId);
for (int i = 0; i < 20; i++) yield return null;
var before = new HashSet<NetworkedItem>(UnityEngine.Object.FindObjectsOfType<NetworkedItem>());
validator.ValidateJob(booklet);
for (float t0 = Time.realtimeSinceStartup; job.State == DV.ThingTypes.JobState.InProgress && Time.realtimeSinceStartup - t0 < 10f;) yield return null;
for (int i = 0; i < 180; i++) yield return null;
Hands.ReleaseControl();
var fresh = UnityEngine.Object.FindObjectsOfType<NetworkedItem>().Where(n => !before.Contains(n) && n.NetId != 0).ToList();
ctx.Result = new { state = job.State.ToString(), printed = string.Join(",", fresh.Select(n => n.name.Replace("(Clone)", "") + "#" + n.NetId)),
    cash = fresh.Select(n => n.GetComponent<IMoney>()).Where(m => m != null).Sum(m => m.Amount) };
