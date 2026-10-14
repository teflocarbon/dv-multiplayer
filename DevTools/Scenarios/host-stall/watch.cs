// Either peer: watch the train for a while and report what broke - couplings, hoses, cocks,
// the lowest brake pipe pressure on any car, and the lowest main reservoir pressure on the loco.
// Args: cars (comma-separated IDs, loco first), seconds.
var cars = ctx.Args["cars"].Split(',').Select(id => Trains.Find(id)).ToList();
float seconds = float.Parse(ctx.Args["seconds"]);
float minPipe = 99f, minMainRes = 99f; int worstUncoupled = 0, worstHoses = 0, worstCocks = 0;
var notes = new List<string>();
for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < seconds;)
{
    yield return null;
    int uncoupled = 0, hoses = 0, cocks = 0;
    for (int k = 0; k < cars.Count - 1; k++)
    {
        var link = new[] { cars[k].frontCoupler, cars[k].rearCoupler }.FirstOrDefault(c => c.coupledTo != null && c.coupledTo.train == cars[k + 1]);
        if (link == null) { uncoupled++; continue; }
        if (link.GetAirHoseConnectedTo() != link.coupledTo) hoses++;
        if (!link.IsCockOpen || !link.coupledTo.IsCockOpen) cocks++;
    }
    if ((uncoupled > worstUncoupled || hoses > worstHoses || cocks > worstCocks) && notes.Count < 6)
        notes.Add($"{Time.realtimeSinceStartup - t0:F1}s: {uncoupled} uncoupled, {hoses} hoses off, {cocks} cocks shut");
    worstUncoupled = Math.Max(worstUncoupled, uncoupled); worstHoses = Math.Max(worstHoses, hoses); worstCocks = Math.Max(worstCocks, cocks);
    minPipe = Mathf.Min(minPipe, cars.Min(c => c.brakeSystem.brakePipePressure));
    minMainRes = Mathf.Min(minMainRes, cars[0].brakeSystem.mainReservoirPressure);
}
ctx.Result = new { worstUncoupled, worstHoses, worstCocks, minPipe = Math.Round(minPipe, 2), minMainRes = Math.Round(minMainRes, 2), notes,
    final = new { pipe = Math.Round(cars.Min(c => c.brakeSystem.brakePipePressure), 2), lastAbs = Dev.Abs(cars.Last().transform.position), speed = Math.Round(Mathf.Abs(cars[0].GetForwardSpeed()), 2) } };
