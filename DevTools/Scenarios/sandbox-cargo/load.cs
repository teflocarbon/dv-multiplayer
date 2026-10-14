// Client: use the comms radio's cargo loader on a car - fill it with the first cargo it can carry,
// or (cargo=none) empty it - through the loader's own load/unload methods. Args: car, cargo.
var car = Trains.Find(ctx.Args["car"]);
var loader = Resources.FindObjectsOfTypeAll<DV.CommsRadioCargoLoader>().First(l => l.gameObject.scene.IsValid());
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
typeof(DV.CommsRadioCargoLoader).GetField("pointedCar", flags).SetValue(loader, car);
bool unload = ctx.Args["cargo"] == "none";
DV.ThingTypes.CargoType_v2 cargo = unload ? null : DV.Globals.G.Types.cargos.First(c => c.IsLoadableOnCarType(car.carLivery.parentType));
typeof(DV.CommsRadioCargoLoader).GetField("selectedCargoType", flags).SetValue(loader, cargo);
typeof(DV.CommsRadioCargoLoader).GetMethod(unload ? "UnloadPointedCar" : "LoadPointedCar", flags).Invoke(loader, null);
for (int i = 0; i < 120; i++) yield return null;
ctx.Result = new { cargo = cargo?.id ?? "none", loaded = car.LoadedCargo.ToString(), amount = car.LoadedCargoAmount };
