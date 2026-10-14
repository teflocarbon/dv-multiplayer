// Either peer: every SECONDS, for each listed car that is moving (> 0.05 m/s) on this peer, its
// speed and (clients) its TrainSpeedQueue state. Args: cars, seconds (30), every (1).
TrainCar Find(string id) { try { return Trains.Find(id); } catch { return null; } }
float seconds = ctx.Args.TryGetValue("seconds", out var s) ? float.Parse(s) : 30f;
float every = ctx.Args.TryGetValue("every", out var e) ? float.Parse(e) : 1f;
var cars = ctx.Args["cars"].Split(',').Select(Find).Where(c => c != null).ToList();
var Q = typeof(Multiplayer.Components.Networking.Train.TrainSpeedQueue);
var F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
object Field(object o, string name) => Q.GetField(name, F)?.GetValue(o);
var rows = new List<string>();
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
{
    var line = new List<string>();
    foreach (var car in cars)
    {
        float v = car.derailed ? car.rb.velocity.magnitude : car.GetForwardSpeed();
        if (Mathf.Abs(v) < 0.05f) continue;
        string q = "";
        var queue = car.GetComponent(Q);
        if (queue != null && !Multiplayer.Components.Networking.NetworkLifecycle.Instance.IsHost())
        {
            float last = (float)Field(queue, "lastSampleTime");
            q = $" tgt={(float)Field(queue, "targetSpeed"):F2} drv={Field(queue, "driving")} corr={(float)Field(queue, "easedCorrection"):F2} age={(Time.time - last):F1}";
        }
        line.Add($"{car.ID} v={v:F2}{(car.derailed ? " D" : "")} sleep={car.rb.IsSleeping()}{q}");
    }
    rows.Add($"{Time.realtimeSinceStartup - t0:F0}s: " + (line.Count == 0 ? "-" : string.Join(" | ", line)));
    yield return new WaitForSecondsRealtime(every);
}
ctx.Result = rows;
