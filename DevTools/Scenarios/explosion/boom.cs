// Either peer: set off the car's fuel explosion (ResourceExplosionBase) or, with kind=cargo, its
// cargo explosion. Args: car, kind.
var car = Trains.Find(ctx.Args["car"]);
bool cargo = ctx.Args.TryGetValue("kind", out var k) && k == "cargo";
if (cargo) (car.interior != null ? car.interior.GetComponentsInChildren<CargoReactionBase>(true) : car.GetComponentsInChildren<CargoReactionBase>(true)).First().ExplodeCargo();
else car.GetComponentsInChildren<ResourceExplosionBase>(true).First().ExplodeResource();
for (int i = 0; i < 30; i++) yield return null;
ctx.Result = new { exploded = car.isExploded };
