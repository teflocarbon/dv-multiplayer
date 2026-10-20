using System;
using Multiplayer.Server.Dedicated;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Players;

public static class CareerOwner
{
    /// <summary>
    /// Who owns this career on this host: the account the game runs under, or -mp-owner (a
    /// SteamID64) for a career copied to a server PC running under another account. On a dedicated
    /// host the owner is an admin without being listed, and the one-time conversion gives them the
    /// old host player's state.
    /// </summary>
    public static Guid Id => PlayerIdentity.TryParse(DedicatedHost.Arg("-mp-owner"), out Guid owner) ? owner : PlayerIdentity.Local;
}
