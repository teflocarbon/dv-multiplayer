namespace Multiplayer.Shared.Session;

/// <summary>
///     A local player session with no transport of its own (the host's, in host-local-session
///     stage 3): the lifecycle polls it each tick where it polls a client, and counts it as
///     applying another player's change while it handles what the server sent it.
/// </summary>
public interface IPolledSession
{
    void PollEvents();

    bool IsProcessingPacket { get; }
}
