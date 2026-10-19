// Client: steps off the loco onto the ground beside it, just further from it than the reach the
// host checks (a car's length from its centre): where a client left behind by a rolling loco was
// (#159). Only just: at 26 m the outside controls unloaded under the next probe (batch 5b), and the
// cab already had at 19.6 m. Returns how far it is and what's loaded.
var car = Trains.Find(ctx.Args["loco"]);
float reach = CarSpawner.Instance.carLiveryToCarLength[car.carLivery];
Vector3 side = Vector3.ProjectOnPlane(car.transform.right, Vector3.up).normalized;
Vector3 to = Dev.Abs(car.transform.position + side * (reach + 1.5f));
yield return LocalPlayer.TeleportToGround(to.x, to.z);
for (int i = 0; i < 30; i++) yield return null;
ctx.Result = new
{
    distance = Math.Round(Vector3.Distance(PlayerManager.PlayerTransform.position, car.transform.position), 1),
    reach = Math.Round(reach, 1),
    onCar = PlayerManager.Car == car,
    cab = car.loadedInterior != null,
    external = car.loadedExternalInteractables != null,
};
