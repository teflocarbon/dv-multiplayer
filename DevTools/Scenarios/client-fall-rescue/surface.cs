// Client: wait for the item, and say where the ground is under it in this game. Args: item.
ushort id = ushort.Parse(ctx.Args["item"]);
NetworkedItem item = null;
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 30f && (!NetworkedItem.TryGet(id, out item) || item == null);) yield return null;
if (item == null) throw new Exception("item " + id + " never arrived");
yield return new WaitForSeconds(2f);
if (NetworkedItemManager.BelowTerrain(item.transform.position, out float surface) == null)
    throw new Exception("no terrain loaded under the item in the client's game");
Vector3 abs = Dev.Abs(item.transform.position);
ctx.Result = new { item = id, surface, x = abs.x, z = abs.z };
