namespace Multiplayer.Shared.Session;

/// <summary>
///     The server as the host's own player reaches it: in-process, with no packets
///     (docs/design/host-local-session.md, stage 2). Shared and Client code can't reference the
///     server's types, so the host's call sites use this, through
///     <see cref="NetworkLifecycle.LocalServer"/>.
/// </summary>
public interface ILocalServer
{
    /// <summary>
    ///     Hands a request from the host's own player to the server, which handles it at its next
    ///     poll, in order, with that player as the sender: the same work as the request's packet
    ///     handler. False, and nothing queued, if the server doesn't handle this request type
    ///     in-process yet: send it through the loopback client as before.
    /// </summary>
    bool Submit<TRequest>(TRequest request) where TRequest : class;

    /// <summary>
    ///     True while the server applies another player's change to its own world (a junction, a
    ///     turntable, a time skip): part of <see cref="NetworkLifecycle.IsProcessingPacket"/>.
    /// </summary>
    bool IsApplyingRemoteChange { get; }

    /// <summary>Players logged in, the host's own (or a dedicated host's service identity) included.</summary>
    int PlayerCount { get; }
}
