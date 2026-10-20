namespace Multiplayer.Shared.Session;

/// <summary>
///     The server's pause rule, read by the shared pause patches through
///     <see cref="NetworkLifecycle.LocalServer"/>. No server here means no pausing.
/// </summary>
public interface IPauseServer
{
    /// <summary>Whether this game may pause: single player, the host playing alone, or an empty dedicated host.</summary>
    bool AllowsPause { get; }
}
