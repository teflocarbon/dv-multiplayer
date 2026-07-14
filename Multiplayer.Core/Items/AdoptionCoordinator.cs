using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Items;

public enum AdoptionBeginStatus
{
    Started,
    InvalidToken,
    ItemAlreadyPending,
    TokenAlreadyPending,
    TokenAlreadyCompleted
}

public enum AdoptionResolutionStatus
{
    Accepted,
    Rejected,
    InvalidAcceptedResult,
    UnknownToken,
    AlreadyCompleted
}

public readonly struct AdoptionResolution<TItem> where TItem : class
{
    public AdoptionResolution(AdoptionResolutionStatus status, TItem item,
        string token, ushort assignedNetId)
    {
        Status = status;
        Item = item;
        Token = token ?? string.Empty;
        AssignedNetId = assignedNetId;
    }

    public AdoptionResolutionStatus Status { get; }
    public TItem Item { get; }
    public string Token { get; }
    public ushort AssignedNetId { get; }
}

/// <summary>Pure client-side correlation between one local object and one adoption token.</summary>
public sealed class AdoptionCoordinator<TItem> where TItem : class
{
    private readonly Dictionary<string, TItem> pendingByToken = new(StringComparer.Ordinal);
    private readonly Dictionary<TItem, string> pendingByItem;
    private readonly HashSet<string> completedTokens = new(StringComparer.Ordinal);

    public AdoptionCoordinator(IEqualityComparer<TItem> itemComparer = null)
    {
        pendingByItem = new Dictionary<TItem, string>(itemComparer ?? EqualityComparer<TItem>.Default);
    }

    public int PendingCount => pendingByToken.Count;

    public AdoptionBeginStatus TryBegin(TItem item, string token)
    {
        if (item == null || string.IsNullOrWhiteSpace(token) || token.Length > 64)
            return AdoptionBeginStatus.InvalidToken;
        if (completedTokens.Contains(token)) return AdoptionBeginStatus.TokenAlreadyCompleted;
        if (pendingByItem.ContainsKey(item)) return AdoptionBeginStatus.ItemAlreadyPending;
        if (pendingByToken.ContainsKey(token)) return AdoptionBeginStatus.TokenAlreadyPending;
        pendingByToken.Add(token, item);
        pendingByItem.Add(item, token);
        return AdoptionBeginStatus.Started;
    }

    public AdoptionResolution<TItem> Resolve(string token, bool accepted, ushort assignedNetId)
    {
        token ??= string.Empty;
        if (completedTokens.Contains(token))
            return new AdoptionResolution<TItem>(AdoptionResolutionStatus.AlreadyCompleted,
                null, token, assignedNetId);
        if (!pendingByToken.TryGetValue(token, out TItem item))
            return new AdoptionResolution<TItem>(AdoptionResolutionStatus.UnknownToken,
                null, token, assignedNetId);

        pendingByToken.Remove(token);
        pendingByItem.Remove(item);
        completedTokens.Add(token);
        AdoptionResolutionStatus status = !accepted
            ? AdoptionResolutionStatus.Rejected
            : assignedNetId == 0
                ? AdoptionResolutionStatus.InvalidAcceptedResult
                : AdoptionResolutionStatus.Accepted;
        return new AdoptionResolution<TItem>(status, item, token, assignedNetId);
    }
}

public enum HostAdoptionBeginStatus
{
    Started,
    InvalidToken,
    AlreadyPending,
    AlreadyCompleted
}

public sealed class HostAdoptionOutcome
{
    public bool Accepted { get; set; }
    public ushort AssignedNetId { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
}

/// <summary>Host-side idempotency registry scoped by authenticated player and token.</summary>
public sealed class HostAdoptionRegistry
{
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostAdoptionOutcome> completed = new(StringComparer.Ordinal);

    public HostAdoptionBeginStatus TryBegin(byte playerId, string token)
    {
        if (playerId == 0 || string.IsNullOrWhiteSpace(token) || token.Length > 64)
            return HostAdoptionBeginStatus.InvalidToken;
        string key = Key(playerId, token);
        if (completed.ContainsKey(key)) return HostAdoptionBeginStatus.AlreadyCompleted;
        if (!pending.Add(key)) return HostAdoptionBeginStatus.AlreadyPending;
        return HostAdoptionBeginStatus.Started;
    }

    public bool TryGetCompleted(byte playerId, string token, out HostAdoptionOutcome outcome) =>
        completed.TryGetValue(Key(playerId, token), out outcome);

    public void Complete(byte playerId, string token, bool accepted, ushort assignedNetId,
        string rejectionReason)
    {
        string key = Key(playerId, token);
        pending.Remove(key);
        completed[key] = new HostAdoptionOutcome
        {
            Accepted = accepted,
            AssignedNetId = assignedNetId,
            RejectionReason = rejectionReason ?? string.Empty
        };
    }

    private static string Key(byte playerId, string token) => $"{playerId}:{token ?? string.Empty}";
}
