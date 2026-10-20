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
    ///     handler. False, and nothing queued, if the server has no in-process handler for this
    ///     request type.
    /// </summary>
    bool Submit<TRequest>(TRequest request) where TRequest : class;

    /// <summary>
    ///     A packet from the host's own player, as bytes the way a connection carries them: only for
    ///     MPAPI mods' packets (<c>IClient.SendPacketToServer</c> on the host), which have no in-process
    ///     handler. The server reads it at its next poll as from that player's peer.
    /// </summary>
    void ReceiveFromLocalPlayer(byte[] data);

    /// <summary>
    ///     True while the server applies another player's change to its own world (a junction, a
    ///     turntable, a time skip): part of <see cref="NetworkLifecycle.IsProcessingPacket"/>.
    /// </summary>
    bool IsApplyingRemoteChange { get; }

    /// <summary>Players logged in, the host's own included (a dedicated host has none).</summary>
    int PlayerCount { get; }
}
