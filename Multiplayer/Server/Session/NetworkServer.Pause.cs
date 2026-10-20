using Multiplayer.Server.Dedicated;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Session;

// NetworkServer's pause rule (see docs/design/code-layout.md).
public partial class NetworkServer : IPauseServer
{
    public bool AllowsPause
    {
        get
        {
            NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
            return IsRunning &&
                (IsSinglePlayer ||
                (lifecycle.IsHostPlayingAlone && lifecycle.IsClientRunning) ||
                DedicatedHost.AllowsPause);
        }
    }
}
