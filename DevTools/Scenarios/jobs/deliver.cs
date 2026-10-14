// Host: stand in for driving a transport job's cars to their destination: set each car's logical
// track to the destination (what vanilla's completion check reads), handbrakes on. Args: job.
var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n.Job?.ID == ctx.Args["job"]);
var task = nj.Job.tasks.First();
var data = task.GetTaskData();
var setter = typeof(DV.Logic.Job.Car).GetProperty("CurrentTrack").GetSetMethod(true);
foreach (var car in data.cars)
{
    setter.Invoke(car, new object[] { data.destinationTrack });
    if (TrainCarRegistry.Instance.logicCarToTrainCar.TryGetValue(car, out var tc)) tc.brakeSystem.SetHandbrakePosition(1f);
}
for (int i = 0; i < 10; i++) yield return null;
ctx.Result = new { task = task.UpdateTaskState().ToString(), cars = data.cars.Count, dest = data.destinationTrack.ID.FullID };
