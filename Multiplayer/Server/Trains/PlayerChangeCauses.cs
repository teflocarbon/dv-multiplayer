using System;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Trains;

namespace Multiplayer.Server.Trains;

/// <summary>Train change causes a player acted (see <see cref="TrainChangeCauses"/>).</summary>
internal static class PlayerChangeCauses
{
    internal static ChangeCause<TReason> Cause<TReason>(TReason reason, ServerPlayer player, string detail = null)
        where TReason : struct, Enum =>
        TrainChangeCauses.Cause(reason, player?.PlayerId ?? 0, player?.Username, detail);
}
