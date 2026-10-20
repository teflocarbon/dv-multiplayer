// Either peer: samples the car every 0.1 s for `seconds`. Coroutine; result: the samples (time,
// absolute position, speed, derailed, kinematic, and on a client whether TrainSpeedQueue is placing).
var car = Trains.Find(ctx.Args["car"]);
float seconds = float.Parse(ctx.Args["seconds"], System.Globalization.CultureInfo.InvariantCulture);
var queue = car.GetComponent<Multiplayer.Client.Trains.TrainSpeedQueue>();
var samples = new List<object>();
float t0 = Time.realtimeSinceStartup;
while (Time.realtimeSinceStartup - t0 < seconds)
{
    var a = Dev.Abs(car.rb.position);
    samples.Add(new
    {
        t = Math.Round(Time.realtimeSinceStartup - t0, 2),
        x = a.x, y = a.y, z = a.z,
        v = Math.Round(car.rb.velocity.magnitude, 2),
        derailed = car.derailed,
        kinematic = car.rb.isKinematic,
        placing = queue != null && queue.Placing,
    });
    yield return new WaitForSecondsRealtime(0.1f);
}
ctx.Result = new { samples };
