namespace Multiplayer.Shared.Session;

/// <summary>
///     What this game is in the session. <see cref="NetworkLifecycle.IsHost()"/> is used for all three
///     of these today; code that means one of them should ask for it by name. See
///     docs/design/code-layout.md.
/// </summary>
public static class NetworkRole
{
    /// <summary>
    ///     This game is the authority: a player host, a dedicated host, or single player (a
    ///     one-player server). The same as <see cref="NetworkLifecycle.IsHost()"/>.
    /// </summary>
    public static bool IsServer
    {
        get
        {
            NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
            return lifecycle != null && lifecycle.IsHost();
        }
    }

    /// <summary>
    ///     This game plays on another game's server, so the world here follows the host's. Never true
    ///     on a host.
    /// </summary>
    public static bool IsRemoteClient
    {
        get
        {
            NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
            return lifecycle != null && lifecycle.IsClientRunning && !lifecycle.IsHost();
        }
    }

    /// <summary>
    ///     Someone plays at this keyboard: a client, or a host that isn't a dedicated host. False when
    ///     no session is running.
    /// </summary>
    public static bool HasLocalPlayer
    {
        get
        {
            NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
            if (lifecycle == null)
                return false;
            return lifecycle.IsHost() ? !lifecycle.IsDedicatedHost : lifecycle.IsClientRunning;
        }
    }

    /// <summary>
    ///     The id of the player at this keyboard (<see cref="ILocalPlayerSession.PlayerId"/>), the same
    ///     on a host and a client; 0 when there's no session or its login hasn't been accepted yet.
    ///     0 on a dedicated host, which has no player. Use this, not
    ///     <c>IsHost() ? server id : client id</c>.
    /// </summary>
    public static byte LocalPlayerId => NetworkLifecycle.Instance?.LocalSession?.PlayerId ?? 0;
}
