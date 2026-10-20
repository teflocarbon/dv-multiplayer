using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Trains;
using UnityEngine;

namespace Multiplayer.Shared.Trains;

/// <summary>
///     What train code asks of the local player's session (docs/design/code-layout.md; contract 4
///     of the split), and the requests a player makes of trains that the host's player makes too.
///     <c>NetworkClient</c> implements it on a client (Client/Session/NetworkClient.Trains.cs), the
///     host's <c>LocalPlayerSession</c> on a player host (LocalPlayerSession.Trains.cs: the same
///     requests, handed to the server in-process). Reached as <see cref="TrainRoles.Client"/>; null
///     on a dedicated host, where nobody plays.
/// </summary>
public interface ITrainClient
{
    /// <summary>Take or give back the lease on a control's port: the player grabbed or let go of it.</summary>
    void SendTrainControlAuthorityRequest(ushort netId, uint portNetId, bool requestAuthority);

    /// <summary>The comms radio deletes a car.</summary>
    void SendTrainDeleteRequest(ushort netId);

    /// <summary>The comms radio puts a car back on the rails.</summary>
    void SendTrainRerailRequest(ushort netId, ushort trackId, Vector3 position, Vector3 forward);

    /// <summary>The comms radio summons a work train; the answer resolves <paramref name="ticketId"/>.</summary>
    void SendWorkTrainRequest(uint ticketId, string liveryId, ushort trackNetId, int position, bool withTrackDirection);

    /// <summary>
    ///     Why a coupling comes apart while a client applies the host's topology: a refused
    ///     coupling of its own, the host's reason if it sent one, or just following the host. False
    ///     when this isn't a client following the host's topology (the host's session included).
    /// </summary>
    bool TryFollowingHostUncoupleCause(Coupler coupler, Coupler other, out ChangeCause<UncoupleReason> cause);

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
