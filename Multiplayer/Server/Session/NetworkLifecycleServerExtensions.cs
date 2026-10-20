using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Session;

public static class NetworkLifecycleServerExtensions
{
    /// <summary>
    ///     Whether the provided player is the host's own (<see cref="NetworkServer.LocalPlayer"/>; a
    ///     dedicated host has none).
    ///     Note that this does NOT check authority, and should only be used for client-only logic.
    ///     An extension so that <see cref="NetworkLifecycle"/>, which is Shared, names no server type.
    /// </summary>
    public static bool IsHost(this NetworkLifecycle lifecycle, ServerPlayer player)
    {
        return player != null && lifecycle.IsServerRunning && NetworkServer.Instance?.LocalPlayer == player;
    }
}
