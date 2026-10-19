// Host: spawn a fresh mug at the carrier's feet (the carrier may be the host or the client).
var who = ctx.Args["who"];
Vector3 at = who == "host"
    ? PlayerManager.PlayerTransform.position
    : UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().transform.position;
var spec = DV.Globals.G.Items.items.First(i => i.ItemPrefabName == "Mug");
var go = UnityEngine.Object.Instantiate(spec.gameObject, at + Vector3.up * 0.6f + Vector3.right * 0.8f, Quaternion.identity, WorldMover.OriginShiftParent);
for (int i = 0; i < 60; i++) yield return null;
ctx.Result = new { item = go.GetComponent<NetworkedItem>().NetId };
