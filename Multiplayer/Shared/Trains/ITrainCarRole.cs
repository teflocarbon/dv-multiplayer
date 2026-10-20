using System.Collections.Generic;
using DV.Customization.Paint;
using LocoSim.Implementations;

namespace Multiplayer.Shared.Trains;

/// <summary>
///     What this game is for a <see cref="NetworkedTrainCar"/>: the server's half on the server
///     (<see cref="ITrainCarServerPart"/>), whose changes to the car are the authority's and are
///     committed, or the local player's half on a client (<see cref="ITrainCarClientPart"/>), whose
///     changes are requests to the host. The car sends its dirty ports, fuses, handbrake, brake
///     release and paint through it, and lets it pick which of them are this game's to send, so the
///     car itself doesn't ask which game it is on. The host's own player has a client part too, but
///     the car's changes there go through the server's.
/// </summary>
public interface ITrainCarRole
{
    /// <summary>Whether a change to this port is this game's to send: every port on the server (its
    /// simulation's feedback included), only the controls players drive on a client.</summary>
    bool Reports(Port port, uint portNetId);

    /// <summary>A port this game reports changed: on a client, the local player may be driving.</summary>
    void NoteLocalControlChange();

    /// <summary>Which of the car's dirty ports to send, and which wait for the host's value to settle.</summary>
    uint[] PortsToSend(HashSet<uint> dirtyPorts, out List<uint> settling);

    /// <summary>Whether a fuse or handbrake change seen here is this game's to send: always the
    /// server's (its simulation's), a client's only when its player could have made it.</summary>
    bool SendsChange(bool viaRemote);

    void SendPorts(uint[] portIds, float[] portValues);

    void SendFuses(uint[] fuseIds, bool[] fuseValues);

    void SendHandbrake(bool viaRemote);

    void SendBrakeCylinderReleased();

    void SendPaintTheme(TrainCarPaint.Target target, uint themeNetId);

    /// <summary>A value the host sent for a port: true if it's handled here (played out or predicted).</summary>
    bool TakeHostPortValue(uint portNetId, Port port, float value, uint hostTick);
}
