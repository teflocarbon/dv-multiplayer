// Either peer: a lighter at the car's leak: the game's own Igniter, as on a lighter or flare item.
var car = Trains.Find(ctx.Args["car"]);
var r = (car.interior != null ? car.interior.GetComponentsInChildren<CargoReactionBase>(true) : car.GetComponentsInChildren<CargoReactionBase>(true)).First(x => x.initialized);
var go = new GameObject("dvmp test lighter");
go.transform.position = r.cargoLeak.Position();
var igniter = go.AddComponent<DV.Hazmat.Igniter>();
igniter.ignitionStrength = 100f; igniter.objectsRadius = 3f;
igniter.IgniteNow();
yield return new WaitForSecondsRealtime(0.5f);
UnityEngine.Object.Destroy(go);
ctx.Result = new { here = r.isIgnited };
