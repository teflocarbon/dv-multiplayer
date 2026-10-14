// Either peer: is the car derailed, is it in the same trainset as its neighbour, where is it.
var car = Trains.Find(ctx.Args["car"]); var other = Trains.Find(ctx.Args["other"]);
return new { derailed = car.derailed, coupled = car.trainset == other.trainset, abs = Dev.Abs(car.transform.position), track = car.Bogies[0].track?.name ?? "-" };
