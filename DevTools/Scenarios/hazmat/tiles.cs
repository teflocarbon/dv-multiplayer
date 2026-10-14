// Either peer: the hazmat tiles near a point (abs x/z, 60 m): count, total liquid, burning tiles,
// and how many burning tiles have fire effects. Args: x, z.
var m = DV.Utils.SingletonBehaviour<HazmatTileManager>.Instance;
var centre = new Vector3(float.Parse(ctx.Args["x"]), 0, float.Parse(ctx.Args["z"]));
var near = m.TileDictionary.Values.Where(t => { var p = m.GetWorldPositionFromGridCoords(m.GetTileCoordX(t.gridPosition), m.GetTileCoordY(t.gridPosition), false); return new Vector2(p.x - centre.x, p.z - centre.z).magnitude < 60f; }).ToList();
return new { enabled = m.enabled, tiles = near.Count, liquid = Math.Round(near.Sum(t => t.liquidContent.Values.Sum())), burning = near.Count(t => t.IsIgnited), flames = near.Count(t => t.IsIgnited && t.terrainFireEffects != null) };
