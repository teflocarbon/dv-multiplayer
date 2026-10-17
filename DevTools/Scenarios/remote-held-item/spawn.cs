// Host: a fresh item (a lantern by default) lying next to the client, as one found in the world.
Multiplayer.Components.Networking.Player.NetworkedPlayer client = null;
for (float t0 = Time.realtimeSinceStartup; (client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Components.Networking.Player.NetworkedPlayer>().FirstOrDefault()) == null && Time.realtimeSinceStartup - t0 < 60f;) yield return null;
if (client == null) throw new Exception("no client avatar");
var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == ctx.Args["prefab"]);
var go = UnityEngine.Object.Instantiate(spec.gameObject, client.transform.position + Vector3.up * 0.6f + Vector3.right * 0.8f, Quaternion.identity, WorldMover.OriginShiftParent);
for (int i = 0; i < 60; i++) yield return null;
ctx.Result = new { item = go.GetComponent<NetworkedItem>().NetId };
