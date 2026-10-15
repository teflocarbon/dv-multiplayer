// Either peer: the state of the listed cars, to compare host against client.
// Args: cars (ids). Per car: absolute position, facing (forward and up), speed, derailed, and the
// ids it is coupled to.
TrainCar Find(string id) { try { return Trains.Find(id); } catch { return null; } }
var result = new Dictionary<string, object>();
foreach (var id in ctx.Args["cars"].Split(','))
{
    var car = Find(id);
    if (car == null) { result[id] = null; continue; }
    var p = Dev.Abs(car.transform.position);
    string Coupled(Coupler cp) => cp?.coupledTo?.train?.ID ?? "";
    var couples = new[] { Coupled(car.frontCoupler), Coupled(car.rearCoupler) }.Where(x => x != "").OrderBy(x => x).ToArray();
    result[id] = new
    {
        x = Math.Round(p.x, 3), y = Math.Round(p.y, 3), z = Math.Round(p.z, 3),
        f = new[] { Math.Round(car.transform.forward.x, 4), Math.Round(car.transform.forward.y, 4), Math.Round(car.transform.forward.z, 4) },
        u = new[] { Math.Round(car.transform.up.x, 4), Math.Round(car.transform.up.y, 4), Math.Round(car.transform.up.z, 4) },
        speed = Math.Round(car.derailed ? car.rb.velocity.magnitude : Mathf.Abs(car.GetForwardSpeed()), 3),
        derailed = car.derailed,
        coupled = string.Join("+", couples)
    };
}
return result;
