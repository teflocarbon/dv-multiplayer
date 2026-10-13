// Client: the motion controller's view of a car every 0.1 s (args: car, seconds): along-track
// error to the projected host position, host target speed, eased speed and correction, actual speed.
var car = Trains.Find(ctx.Args["car"]);
var q = car.GetComponent<Multiplayer.Components.Networking.Train.TrainSpeedQueue>();
var nb = car.Bogies[0].GetComponent<Multiplayer.Components.Networking.Train.NetworkedBogie>();
var f = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var tTarget = typeof(Multiplayer.Components.Networking.Train.TrainSpeedQueue).GetField("targetSpeed", f);
var tCmd = typeof(Multiplayer.Components.Networking.Train.TrainSpeedQueue).GetField("easedSpeed", f);
var tCor = typeof(Multiplayer.Components.Networking.Train.TrainSpeedQueue).GetField("easedCorrection", f);
var tDrive = typeof(Multiplayer.Components.Networking.Train.TrainSpeedQueue).GetField("driving", f);
var s = new List<string>();
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < float.Parse(ctx.Args["seconds"]);)
{
    for (float t1 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t1 < 0.1f;) yield return null;
    nb.TryGetAlongTrackError(car, out float e);
    s.Add($"{Time.realtimeSinceStartup - t0:F1}:e{e:F2} tgt{(float)tTarget.GetValue(q):F1} spd{(float)tCmd.GetValue(q):F1} cor{(float)tCor.GetValue(q):F1} v{car.GetForwardSpeed():F1}{((bool)tDrive.GetValue(q) ? "" : " free")}");
}
ctx.Result = new { s };
