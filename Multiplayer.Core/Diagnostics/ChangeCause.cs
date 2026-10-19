using System;

namespace Multiplayer.Core.Diagnostics;

/// <summary>
/// Why something changed (a coupling came apart, a car derailed or was removed, an item moved):
/// a reason from that system's own enum, the player whose action did it, the network tick, and
/// free text for what the reason alone doesn't say (a refusal's measured distance, the methods
/// that called an unexplained change). Systems keep the last cause on the thing that changed, log
/// it in one format, and send it to clients with the change, so "why" can be answered afterwards
/// on any peer instead of reconstructed from timings.
/// </summary>
public readonly struct ChangeCause<TReason> where TReason : struct, Enum
{
    public readonly TReason Reason;
    /// <summary>The acting player's id, or 0 when no player did it.</summary>
    public readonly byte PlayerId;
    public readonly uint Tick;
    private readonly string playerName;
    private readonly string detail;

    public ChangeCause(TReason reason, byte playerId = 0, string playerName = null, uint tick = 0,
        string detail = null)
    {
        Reason = reason;
        PlayerId = playerId;
        this.playerName = playerName;
        Tick = tick;
        this.detail = detail;
    }

    /// <summary>The acting player's name when it happened, kept because they may since have left.</summary>
    public string PlayerName => playerName ?? string.Empty;
    public string Detail => detail ?? string.Empty;

    public bool HasPlayer => PlayerId != 0 || PlayerName.Length > 0;

    public ChangeCause<TReason> WithDetail(string detail) =>
        new(Reason, PlayerId, PlayerName, Tick, detail);

    /// <summary>"CarDeleted by Bentgear29 (detail)", the form every log line uses.</summary>
    public override string ToString()
    {
        string text = Reason.ToString();
        if (PlayerName.Length > 0)
            text += " by " + PlayerName;
        else if (PlayerId != 0)
            text += " by player " + PlayerId;
        if (Detail.Length > 0)
            text += " (" + Detail + ")";
        return text;
    }
}
