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
}

public static class TrainRoles
{
    /// <summary>The server, when this game is one; null otherwise.</summary>
    public static ITrainServer Server => NetworkLifecycle.Instance?.LocalServer as ITrainServer;
}
