// Host: spawn a fresh item of the given prefab at the client's feet. Args: prefab.
var player = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First();
var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == ctx.Args["prefab"]);
var go = UnityEngine.Object.Instantiate(spec.gameObject, player.transform.position + Vector3.up * 0.6f + Vector3.right * 0.8f,
    Quaternion.identity, WorldMover.OriginShiftParent);
for (int i = 0; i < 60; i++) yield return null;
ctx.Result = new { item = go.GetComponent<NetworkedItem>().NetId };
