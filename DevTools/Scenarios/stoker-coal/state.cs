// Either peer: the loco's stoker rate (normalised; -1 without a stoker) and firebox contents.
var car = Trains.Find(ctx.Args["loco"]);
var stoker = car.SimController.SimulationFlow.OrderedSimComps.FirstOrDefault(c => c.GetType().Name == "SteamMechanicalStoker");
var readout = stoker == null ? null : (LocoSim.Implementations.Port)stoker.GetType().GetField("StokingNormalizedReadout").GetValue(stoker);
return new
{
    stoking = readout == null ? -1 : Math.Round(readout.Value, 3),
    firebox = Math.Round(car.SimController.firebox.FireboxContents, 2)
};
