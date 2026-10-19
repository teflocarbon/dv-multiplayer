// Host: spawn a fresh item of the given prefab next to the client (or the host itself, near=host).
Multiplayer.Shared.Players.NetworkedPlayer client = null;
float joinWait = Time.realtimeSinceStartup;
while ((client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().FirstOrDefault()) == null && Time.realtimeSinceStartup - joinWait < 60f) yield return null; // the client may still be joining
if (client == null) throw new Exception("no client avatar");
Vector3 at = ctx.Args.TryGetValue("near", out string near) && near == "host"
    ? PlayerManager.PlayerTransform.position : client.transform.position;
var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == ctx.Args["prefab"]);
var go = UnityEngine.Object.Instantiate(spec.gameObject, at + Vector3.up * 0.6f + Vector3.right * 0.8f, Quaternion.identity, WorldMover.OriginShiftParent);
go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
for (int i = 0; i < 60; i++) yield return null;
ctx.Result = new { item = go.GetComponent<NetworkedItem>().NetId };
