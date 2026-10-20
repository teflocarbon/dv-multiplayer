using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Trains;

/// <summary>
///     What Shared train code asks of the server, which it can't reference (docs/design/code-layout.md).
///     <c>NetworkServer</c> implements it (Server/Session/NetworkServer.Trains.cs). Most server work
///     belongs in Server code; this is only for what Shared code observes and the server has to hear.
/// </summary>
public interface ITrainServer
{
    /// <summary>Why a coupling came apart, sent with the next topology snapshot.</summary>
    void NoteUncouple(Coupler coupler, ChangeCause<UncoupleReason> cause);

    /// <summary>The server's half of a car (Server/Trains/NetworkedTrainCarServer.cs), made with it.</summary>
    ITrainCarServerPart CreateCarPart(NetworkedTrainCar car);
}

/// <summary>
///     The server's half of a <see cref="NetworkedTrainCar"/>: its authority (leases, coupler
///     interactions, validation) and what it sends players. The car calls it at the points that
///     code always ran, so splitting it out changed no order. On the server it is also the car's
///     <see cref="ITrainCarRole"/>: the host's own control changes (its simulation's, its player's
///     levers) are committed through it.
/// </summary>
public interface ITrainCarServerPart : ITrainCarRole
{
    /// <summary>At the end of the car's Start.</summary>
    void Start();

    /// <summary>First thing in the car's OnDisable, a retired car's too.</summary>
    void Unregister();

    /// <summary>In the car's OnDisable, when it stops tracking its events.</summary>
    void Disable();

    /// <summary>Every network tick, after the car's own.</summary>
    void Tick(uint tick);
}

public static class TrainRoles
{
    /// <summary>The server, when this game is one; null otherwise.</summary>
    public static ITrainServer Server => NetworkLifecycle.Instance?.LocalServer as ITrainServer;

    /// <summary>The local player's session, when it does trains; null when nobody plays here (none
    /// on a dedicated host).</summary>
    public static ITrainClient Client => NetworkLifecycle.Instance?.LocalSession as ITrainClient;
}
