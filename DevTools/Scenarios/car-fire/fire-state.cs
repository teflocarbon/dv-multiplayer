// Either peer: the car's cargo fire state on this game, from its first set-up cargo reaction.
var car = Trains.Find(ctx.Args["car"]);
var r = (car.interior != null ? car.interior.GetComponentsInChildren<CargoReactionBase>(true) : car.GetComponentsInChildren<CargoReactionBase>(true)).FirstOrDefault(x => x.initialized);
if (r == null) return new { car = car.ID, reaction = "none initialised", exploded = car.isExploded };
return new { car = car.ID, leaking = r.cargoLeak != null && r.cargoLeak.IsLeaking(), ignited = r.isIgnited, aboutToExplode = r.aboutToExplode, exploded = car.isExploded, health = Math.Round(car.CarDamage.currentHealth) };
