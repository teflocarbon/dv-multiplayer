// Either peer: take a job's overview and put it in the nearest job validator, as accepting a job
// in the game does; wait for the job to start. Args: job (job ID).
var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n.Job?.ID == ctx.Args["job"]);
var job = nj.Job;
var overviewItem = nj.JobOverview ?? throw new Exception("no overview for " + job.ID);
var validator = UnityEngine.Object.FindObjectsOfType<JobValidator>().OrderBy(v => Vector3.Distance(v.transform.position, PlayerManager.PlayerTransform.position)).First();
if (Hands.Held != null && Hands.Held.NetId != overviewItem.NetId) yield return Hands.Drop();
if (Hands.Held == null) yield return Hands.Pickup(overviewItem.NetId);
for (int i = 0; i < 20; i++) yield return null;
var overview = overviewItem.GetComponent<JobOverview>();
var before = new HashSet<JobBooklet>(UnityEngine.Object.FindObjectsOfType<JobBooklet>());
validator.ProcessJobOverview(overview);
for (float t0 = Time.realtimeSinceStartup; job.State == DV.ThingTypes.JobState.Available && Time.realtimeSinceStartup - t0 < 10f;) yield return null;
for (int i = 0; i < 120; i++) yield return null;
Hands.ReleaseControl();
var booklets = UnityEngine.Object.FindObjectsOfType<JobBooklet>().Where(b => b.job == job).ToList();
ctx.Result = new { state = job.State.ToString(), overviewGone = overviewItem == null || !overviewItem.gameObject.activeInHierarchy,
    booklets = string.Join(",", booklets.Select(b => (b.GetComponent<NetworkedItem>()?.NetId ?? 0) + (before.Contains(b) ? "(old)" : ""))) };
