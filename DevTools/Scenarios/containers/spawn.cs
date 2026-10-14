// Host: spawn a fresh item of the given prefab next to the client.
Multiplayer.Components.Networking.Player.NetworkedPlayer client = null;
float joinWait = Time.realtimeSinceStartup;
while ((client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.Player.NetworkedPlayer>().FirstOrDefault()) == null && Time.realtimeSinceStartup - joinWait < 60f) yield return null; // the client may still be joining
if (client == null) throw new Exception("no client avatar");
Vector3 at = client.transform.position;
var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == ctx.Args["prefab"]);
var go = UnityEngine.Object.Instantiate(spec.gameObject, at + Vector3.up * 0.6f + Vector3.right * 0.8f, Quaternion.identity, WorldMover.OriginShiftParent);
go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
for (int i = 0; i < 60; i++) yield return null;
ctx.Result = new { item = go.GetComponent<NetworkedItem>().NetId };
