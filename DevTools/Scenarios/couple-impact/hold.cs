// Client: how its TrainSpeedQueue is holding the loco and the wagon, for when they're found off the
// host's (#175: after a 3 m/s impact both stood coupled 1.1 m from the host's until it pulled
// away). A stopped coupled car is driven back at 0.2-0.5 m/s (HoldCoupled); one that won't move
// in 10 s is left for 10 s before another try. Per car: the queue's state, its along-track error
// to the host's bogies, and what might stop it moving (kinematic, asleep, braked).
object Of(string id)
{
    var car = Trains.Find(id);
    var q = car.GetComponent<Multiplayer.Shared.Trains.TrainSpeedQueue>();
    if (q == null) return new { car = id, queue = "none" };
    var args = new object[] { 0f };
    bool hasError = (bool)typeof(Multiplayer.Shared.Trains.TrainSpeedQueue)
        .GetMethod("TryGetAlongTrackError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Invoke(q, args);
    float Since(float t) => float.IsNegativeInfinity(t) ? -1f : (float)Math.Round(Time.time - t, 1);
    return new
    {
        car = id,
        held = q.Held, following = q.Following, placing = q.Placing, parked = q.Parked, hostCoupled = q.HostCoupled,
        driving = q.driving,
        error = hasError ? Math.Round((float)args[0], 3) : (double?)null,
        hostSpeed = Math.Round(q.targetSpeed, 3),
        sampleAge = Since(q.lastSampleTime),
        drivingFor = Since(q.holdDriveSince),
        retryIn = float.IsNegativeInfinity(q.holdRetryAt) ? -1f : (float)Math.Round(q.holdRetryAt - Time.time, 1),
        offFor = Since(q.holdOffSince),
        kinematic = car.rb.isKinematic,
        asleep = car.rb.IsSleeping(),
        speed = Math.Round(car.rb.velocity.magnitude, 3),
        handbrake = car.brakeSystem.hasHandbrake ? Math.Round(car.brakeSystem.handbrakePosition, 2) : -1,
        braking = Math.Round(car.brakeSystem.brakingFactor, 2),
        setSize = car.trainset?.cars.Count ?? 0,
    };
}
return new { loco = Of(ctx.Args["loco"]), wagon = Of(ctx.Args["wagon"]), now = Math.Round(Time.time, 1) };
