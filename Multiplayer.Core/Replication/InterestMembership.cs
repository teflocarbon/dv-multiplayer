namespace Multiplayer.Core.Replication;

public enum RelevanceAction
{
    None,
    Enter,
    Refresh,
    Retain,
    Leave
}

public readonly struct RelevanceDecision
{
    public RelevanceDecision(RelevanceAction action, bool isRelevant)
    {
        Action = action;
        IsRelevant = isRelevant;
    }

    public RelevanceAction Action { get; }
    public bool IsRelevant { get; }
}

public static class InterestMembershipEvaluator
{
    public static RelevanceDecision Evaluate(
        bool currentlyRelevant,
        double squaredDistance,
        double maximumSquaredDistance,
        double secondsSinceLastRelevant,
        double removalDelaySeconds)
    {
        if (squaredDistance <= maximumSquaredDistance)
            return new RelevanceDecision(
                currentlyRelevant ? RelevanceAction.Refresh : RelevanceAction.Enter, true);

        if (!currentlyRelevant)
            return new RelevanceDecision(RelevanceAction.None, false);

        if (secondsSinceLastRelevant > removalDelaySeconds)
            return new RelevanceDecision(RelevanceAction.Leave, false);

        return new RelevanceDecision(RelevanceAction.Retain, true);
    }
}

public enum ItemDeliveryKind
{
    None,
    Create,
    Dirty,
    FullSync,
    Destroy
}

public readonly struct ItemDeliveryDecision
{
    public ItemDeliveryDecision(ItemDeliveryKind kind, bool markKnown, bool suppressPayload = false)
    {
        Kind = kind;
        MarkKnown = markKnown;
        SuppressPayload = suppressPayload;
    }

    public ItemDeliveryKind Kind { get; }
    public bool MarkKnown { get; }
    public bool SuppressPayload { get; }
}

public static class KnownItemDeliveryEvaluator
{
    public static ItemDeliveryDecision Evaluate(
        bool relevant,
        bool known,
        uint knownTick,
        uint lastDirtyTick,
        bool hasDirtySnapshot,
        bool suppressCreate,
        bool destroyed)
    {
        if (destroyed)
            return known
                ? new ItemDeliveryDecision(ItemDeliveryKind.Destroy, false)
                : new ItemDeliveryDecision(ItemDeliveryKind.None, false);

        if (!relevant)
            return new ItemDeliveryDecision(ItemDeliveryKind.None, false);

        if (!known)
            return new ItemDeliveryDecision(ItemDeliveryKind.Create, true, suppressCreate);

        if (hasDirtySnapshot)
            return new ItemDeliveryDecision(ItemDeliveryKind.Dirty, true);

        if (knownTick < lastDirtyTick)
            return new ItemDeliveryDecision(ItemDeliveryKind.FullSync, true);

        return new ItemDeliveryDecision(ItemDeliveryKind.None, false);
    }
}
