namespace Multiplayer.Shared.Api;

/// <summary>
///     What MPAPI's <see cref="APIProvider"/> asks the server, through
///     <see cref="Multiplayer.Shared.Session.NetworkLifecycle.LocalServer"/>. <c>NetworkServer</c>
///     implements it, in <c>Server/Session/NetworkServer.Api.cs</c>.
/// </summary>
public interface IApiServer
{
    /// <summary>Whether the server was started for single player.</summary>
    bool IsSinglePlayer { get; }
}
