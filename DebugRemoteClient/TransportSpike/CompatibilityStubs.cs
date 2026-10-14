// These are compile-time-only shims for source files shared with the Unity mod. The spike never
// creates a Settings instance or accesses Multiplayer's Unity lifecycle; SteamWorksTransport only
// needs the signatures because UpdateSettings is part of ITransport.
namespace Multiplayer;

public sealed class Settings
{
    public bool SimulatePacketLoss { get; set; }
    public int SimulationPacketLossChance { get; set; }
    public bool SimulateLatency { get; set; }
    public int SimulationMinLatency { get; set; }
    public int SimulationMaxLatency { get; set; }
}

public static class Multiplayer
{
    public static void LogDebug(System.Func<object> resolver) { }
    public static void Log(object message) { }
    public static void LogWarning(object message) { }
}
