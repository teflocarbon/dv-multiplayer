// Either peer: set the tile at a point (abs x/z) alight. Args: x, z.
var m = DV.Utils.SingletonBehaviour<HazmatTileManager>.Instance;
var world = new Vector3(float.Parse(ctx.Args["x"]), 0, float.Parse(ctx.Args["z"])) + WorldMover.currentMove;
bool ok = m.IgniteTile(m.GetGridPositionFromWorldPosition(world), 100000f);
for (int i = 0; i < 10; i++) yield return null;
ctx.Result = new { accepted = ok };
