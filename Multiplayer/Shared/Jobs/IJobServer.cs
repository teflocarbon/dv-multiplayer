using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Jobs;

/// <summary>
///     The server's side of jobs, as Shared code reaches it without referencing the server's types
///     (docs/design/code-layout.md). <c>NetworkServer</c> implements it, in
///     <c>Server/Session/NetworkServer.Jobs.cs</c>.
/// </summary>
public interface IJobServer
{
    /// <summary>A job was abandoned, completed or expired in the server's world.</summary>
    void OnJobEnded(NetworkedJob job);
}

public static class JobServer
{
    /// <summary>This game's server, or null when it isn't one.</summary>
    public static IJobServer Current => NetworkLifecycle.Instance?.LocalServer as IJobServer;
}
