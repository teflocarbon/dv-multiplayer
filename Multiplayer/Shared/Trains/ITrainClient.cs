using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;

namespace Multiplayer.Shared.Trains;

/// <summary>
///     What Shared train code asks of the local player's session, which it can't reference
///     (docs/design/code-layout.md; contract 4 of the split). <c>NetworkClient</c> implements it
///     (Client/Session/NetworkClient.Trains.cs), reached as <see cref="TrainRoles.Client"/>.
/// </summary>
public interface ITrainClient
{
    /// <summary>The local player's name, for the causes of the changes they make.</summary>
    string Username { get; }

    /// <summary>Set while a refused coupling's snapshot is applied: why its couplings come apart.</summary>
    ChangeCause<UncoupleReason>? RefusalUncoupleCause { get; }

    /// <summary>The host's reason for an uncouple this client is following, if it sent one.</summary>
    bool TakeHostUncoupleCause(Coupler coupler, Coupler other, out ChangeCause<UncoupleReason> cause);

    /// <summary>The local player's half of a car (Client/Trains/NetworkedTrainCarClient.cs), made with it.</summary>
    ITrainCarClientPart CreateCarPart(NetworkedTrainCar car);
}

/// <summary>
///     The local player's half of a <see cref="NetworkedTrainCar"/>, on every game someone plays
///     at, the host's too: the player's controls and chain interactions, and on a client, following
///     the host. The car calls it at the points that code always ran; on a client it is also the
///     car's <see cref="ITrainCarRole"/>.
/// </summary>
public interface ITrainCarClientPart : ITrainCarRole
{
    /// <summary>At the end of the car's Start.</summary>
    void Start();

    /// <summary>In the car's OnDisable.</summary>
    void Disable();

    /// <summary>Another player holds the lease on this control's port (on the host too: its player's levers).</summary>
    bool IsBlocked(uint portNetId);

    /// <summary>The host's values are being written to the car's ports, as they land or played out.</summary>
    bool WritingHostValues { get; }
}
