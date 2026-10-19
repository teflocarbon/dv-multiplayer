// Host: spawn a fresh item of the given prefab at the host's own feet. Args: prefab.
var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == ctx.Args["prefab"]);
var go = UnityEngine.Object.Instantiate(spec.gameObject,
    PlayerManager.PlayerTransform.position + Vector3.up * 0.6f + PlayerManager.PlayerTransform.right * 0.8f,
    Quaternion.identity, WorldMover.OriginShiftParent);
for (int i = 0; i < 60; i++) yield return null;
ctx.Result = new { item = go.GetComponent<NetworkedItem>().NetId, at = Dev.Abs(go.transform.position).ToString() };
