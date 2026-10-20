using System.Collections.Generic;

namespace Multiplayer.Shared.Debugging;

/// <summary>
///     The server's debug data for the in-game observability overlay, read through
///     <see cref="Multiplayer.Shared.Session.NetworkLifecycle.LocalServer"/>. <c>NetworkServer</c>
///     implements it, in <c>Server/Session/NetworkServer.Debugging.cs</c>.
/// </summary>
public interface IDebugServer
{
    /// <summary>Per player: whether the server would send them the item now, and why (<see cref="DebugDiagnostics.InterestMatrix"/>).</summary>
    Dictionary<string, object>[] InterestMatrix(ushort itemNetId);
}
