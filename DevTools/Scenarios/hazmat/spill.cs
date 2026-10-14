// Host or client: pour liquid onto the ground at a point (abs x/z). Args: x, z, amount, cargo.
var m = DV.Utils.SingletonBehaviour<HazmatTileManager>.Instance;
var abs = new Vector3(float.Parse(ctx.Args["x"]), 0, float.Parse(ctx.Args["z"]));
var world = abs + WorldMover.currentMove;
var cargo = (DV.ThingTypes.CargoType)Enum.Parse(typeof(DV.ThingTypes.CargoType), ctx.Args["cargo"]);
m.UpdateTileLiquidToBeAddedDictionary(world, float.Parse(ctx.Args["amount"]), cargo);
for (int i = 0; i < 10; i++) yield return null;
ctx.Result = new { tile = m.GetGridPositionFromWorldPosition(world) };
