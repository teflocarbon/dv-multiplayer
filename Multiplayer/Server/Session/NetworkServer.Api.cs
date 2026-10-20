using Multiplayer.Shared.Api;

namespace Multiplayer.Server.Session;

// NetworkServer's answers to MPAPI's APIProvider (see docs/design/code-layout.md).
public partial class NetworkServer : IApiServer
{
    bool IApiServer.IsSinglePlayer => IsSinglePlayer;
}
