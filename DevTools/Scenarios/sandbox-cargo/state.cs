var car = Trains.Find(ctx.Args["car"]);
return new { loaded = car.LoadedCargo.ToString(), amount = car.LoadedCargoAmount };
