// Either peer: the loco's compressor activation signal port, what the brake system wants it to
// be, and the main reservoir pressure. With a value arg, first writes the port (as anything that
// isn't the brake system would: an echo of a control change).
var car = Trains.Find(ctx.Args["loco"]);
var flow = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>().simulationFlow;
var compressor = car.GetComponentsInChildren<DV.Simulation.Brake.CompressorSimController>(true).FirstOrDefault()
    ?? throw new Exception(car.ID + " has no compressor");
if (!flow.TryGetPort(compressor.activationSignalExtInPortId, out var port))
    throw new Exception("no port " + compressor.activationSignalExtInPortId);
if (ctx.Args.TryGetValue("value", out var v))
    port.ExternalValueUpdate(float.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
return new
{
    port = port.Value,
    wanted = car.brakeSystem.compressorActivationSignal ? "on" : "off",
    pressure = Math.Round(car.brakeSystem.mainReservoirPressure, 2)
};
