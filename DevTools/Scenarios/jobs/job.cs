// Either peer: a job's state, its booklets (NetId and who holds them) and the overview.
var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().FirstOrDefault(n => n.Job?.ID == ctx.Args["job"]);
var job = nj?.Job;
if (job == null) return new { state = "gone", booklets = "", overview = "", money = DV.InventorySystem.Inventory.Instance.PlayerMoney };
var booklets = UnityEngine.Object.FindObjectsOfType<JobBooklet>().Where(b => b.job == job).Select(b => b.GetComponent<NetworkedItem>()).Where(n => n != null)
    .Select(n => n.NetId + ":" + n.GetItemState());
var ov = nj?.JobOverview;
return new { state = job.State.ToString(), booklets = string.Join(",", booklets), overview = ov == null ? "none" : ov.NetId + ":" + ov.GetItemState(),
    money = DV.InventorySystem.Inventory.Instance.PlayerMoney };
