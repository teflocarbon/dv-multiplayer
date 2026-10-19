using System.Collections.Generic;

namespace Multiplayer.Core.Trains;

/// <summary>Who sets a car's simulation port, as far as syncing it goes.</summary>
public enum ControlPortWriter
{
    /// <summary>Not typed CONTROL: only the host's simulation writes it.</summary>
    NotAControl,
    /// <summary>Typed CONTROL, but nothing a player handles drives it: DV or a mod's simulation
    /// derives it from other ports (a horn fed from its control, a gearbox's gear input).</summary>
    Simulation,
    /// <summary>A control a player handles drives it: a lever, valve, window, oiling point.</summary>
    Player,
    /// <summary>Typed CONTROL, but the host keeps it matched to its own simulation state (the
    /// compressor's activation signal). A client never writes it.</summary>
    HostKept,
}

/// <summary>A player's change to a control that took no lease, as a client sees it.</summary>
public enum UnleasedChange
{
    /// <summary>Not a change to send: feedback from the simulation or the host.</summary>
    No,
    /// <summary>Differs from the host's value, but the host only just set it: the control may still
    /// be catching up. Look again later.</summary>
    Settling,
    Send,
    /// <summary>Differs from the host's value, but this player can't make the change: out of reach
    /// of the car, or another player holds the control. Not sent, so the control goes back to the
    /// host's value: left as it is, it would show only on this game, for good (#159).</summary>
    PutBack,
}

/// <summary>
/// Which of a car's CONTROL-typed ports players drive. Several CONTROL ports are written by
/// simulation code instead (playtest 2: a client's copy of the DE6's compressor signal, sent as a
/// player's change, overwrote the host's and the loco never built air). Players' games send only
/// the ones a player can drive; the host takes nothing else from them.
/// </summary>
public sealed class ControlPortWriters<TPort>
{
    private readonly HashSet<TPort> controls;
    private readonly HashSet<TPort> player;
    private readonly HashSet<TPort> hostKept;

    /// <param name="controlPorts">The car's ports typed CONTROL.</param>
    /// <param name="interactablePorts">Ports the car's interactables write (interior and car-body
    /// controls, keyboard inputs, oiling points), from the car's prefabs.</param>
    /// <param name="hostKeptPorts">CONTROL ports the host keeps matched to its simulation.</param>
    public ControlPortWriters(IEnumerable<TPort> controlPorts, IEnumerable<TPort> interactablePorts, IEnumerable<TPort> hostKeptPorts)
    {
        controls = new HashSet<TPort>(controlPorts);
        hostKept = new HashSet<TPort>(hostKeptPorts);
        hostKept.IntersectWith(controls);
        player = new HashSet<TPort>();
        foreach (TPort port in interactablePorts)
            AddPlayerPort(port);
    }

    public int PlayerPortCount => player.Count;

    public IEnumerable<TPort> PlayerPorts => player;

    /// <summary>A control hooked at runtime (a cab loaded after the prefab scan): players drive it.
    /// Ignored for a port that isn't a control or that the host keeps.</summary>
    public bool AddPlayerPort(TPort port) =>
        controls.Contains(port) && !hostKept.Contains(port) && player.Add(port);

    public ControlPortWriter Classify(TPort port)
    {
        if (!controls.Contains(port))
            return ControlPortWriter.NotAControl;
        if (hostKept.Contains(port))
            return ControlPortWriter.HostKept;
        return player.Contains(port) ? ControlPortWriter.Player : ControlPortWriter.Simulation;
    }

    /// <summary>Whether a player's game reports a local change to this port at all.</summary>
    public bool ClientReports(TPort port) => Classify(port) == ControlPortWriter.Player;

    /// <summary>
    /// Whether the host takes a player's write to this port. A control a player drives, or one
    /// they hold the lease on (a control their game hooked that this game's scan missed).
    /// </summary>
    public bool HostAccepts(TPort port, bool senderHoldsLease)
    {
        ControlPortWriter writer = Classify(port);
        return writer == ControlPortWriter.Player || (writer == ControlPortWriter.Simulation && senderHoldsLease);
    }

    /// <summary>
    /// Whether a client sends a change to a control nobody took the lease of (a VR valve, a cab
    /// window, an oiling point: they don't all raise Grabbed or Used), or puts it back. Feedback
    /// is told apart by its value: once settled it's what the host last sent.
    /// </summary>
    /// <param name="hostValue">The port's value as the host last had it; null if never heard.</param>
    /// <param name="secondsSinceHostValue">Since the host last updated the port; null if never.</param>
    public UnleasedChange Unleased(TPort port, bool blocked, bool playerCanTouch, float value, float? hostValue,
        float? secondsSinceHostValue, float tolerance, float settleSeconds)
    {
        if (!ClientReports(port) || hostValue == null)
            return UnleasedChange.No;
        if (System.Math.Abs(value - hostValue.Value) <= tolerance)
            return UnleasedChange.No;
        if (blocked || !playerCanTouch)
            return UnleasedChange.PutBack;
        if (secondsSinceHostValue != null && secondsSinceHostValue.Value < settleSeconds)
            return UnleasedChange.Settling;
        return UnleasedChange.Send;
    }
}
