// Host: spawn a fresh item of the given prefab next to the client's avatar, kinematic, so the
// host's copy stays where the run puts it (the host may have no ground there). Args: prefab.
Multiplayer.Shared.Players.NetworkedPlayer client = null;
float joinWait = Time.realtimeSinceStartup;
while ((client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().FirstOrDefault()) == null && Time.realtimeSinceStartup - joinWait < 60f) yield return null;
if (client == null) throw new Exception("no client avatar");
var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == ctx.Args["prefab"]);
var go = UnityEngine.Object.Instantiate(spec.gameObject, client.transform.position + Vector3.up * 0.3f + client.transform.right * 2f,
    Quaternion.identity, WorldMover.OriginShiftParent);
Rigidbody body = go.GetComponent<Rigidbody>() ?? go.GetComponentInChildren<Rigidbody>();
if (body != null) body.isKinematic = true;
for (int i = 0; i < 60; i++) yield return null;
ctx.Result = new { item = go.GetComponent<NetworkedItem>().NetId };
