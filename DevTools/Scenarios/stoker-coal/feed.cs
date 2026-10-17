// Either peer: feed the loco's firebox for `feed` seconds without a shovel.
//   mode=real (host): fire the loco up (DV's StartupHelper: coal, fire, boiler pressure) and open
//     its Custom Car Loader stoker (its CONTROL port to 1); the stoker then runs on every game.
//   mode=simulate: write the firebox's coal input port each physics step, the way a stoker does.
// Returns whether the stoker ran (its normalised rate went above zero), its rate and the firebox's
// contents at the end.
var car = Trains.Find(ctx.Args["loco"]);
var firebox = car.SimController.firebox ?? throw new Exception(car.ID + " has no firebox");
float feed = float.Parse(ctx.Args["feed"], System.Globalization.CultureInfo.InvariantCulture);
var flow = car.SimController.SimulationFlow;
var stoker = flow.OrderedSimComps.FirstOrDefault(c => c.GetType().Name == "SteamMechanicalStoker");
Func<string, LocoSim.Implementations.Port> stokerPort = name =>
{
    object member = stoker.GetType().GetField(name).GetValue(stoker);
    return member as LocoSim.Implementations.Port
        ?? (LocoSim.Implementations.Port)member.GetType().GetField("port", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(member);
};
float maxRate = 0f;
if (ctx.Args["mode"] == "real")
{
    if (stoker == null) throw new Exception(car.ID + " has no mechanical stoker");
    DV.Simulation.Controllers.StartupHelper.Startup(car);
    yield return new WaitForSeconds(2f);
    stokerPort("Control").ExternalValueUpdate(1f);
    for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < feed; )
    {
        maxRate = Mathf.Max(maxRate, stokerPort("StokingNormalizedReadout").Value);
        yield return null;
    }
}
else
{
    int i = 0;
    for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < feed; i++)
    {
        firebox.fireboxCoalControlPort.Value = i % 2 == 0 ? 0.01f : 0.02f;
        yield return new WaitForFixedUpdate();
    }
}
ctx.Result = new
{
    ran = maxRate > 0.01f,
    stoking = stoker == null ? -1 : Math.Round(stokerPort("StokingNormalizedReadout").Value, 3),
    firebox = Math.Round(firebox.FireboxContents, 2)
};
