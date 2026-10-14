namespace Multiplayer.Core.Items;

public enum InventoryRecallRoute : byte
{
    Vanilla,
    BlockForeignOwner,
    BlockNotRecallable,
    NetworkRecall,
    LostAndFoundRetrieval
}

public readonly struct InventoryPresentationInput
{
    public InventoryPresentationInput(ushort netId, byte persistentOwnerPlayerId,
        byte localPlayerId, bool retrievalEligible, bool hasRetrievalClaim, bool isLostAndFound)
    {
        NetId = netId;
        PersistentOwnerPlayerId = persistentOwnerPlayerId;
        LocalPlayerId = localPlayerId;
        RetrievalEligible = retrievalEligible;
        HasRetrievalClaim = hasRetrievalClaim;
        IsLostAndFound = isLostAndFound;
    }

    public ushort NetId { get; }
    public byte PersistentOwnerPlayerId { get; }
    public byte LocalPlayerId { get; }
    public bool RetrievalEligible { get; }
    public bool HasRetrievalClaim { get; }
    public bool IsLostAndFound { get; }
}

public readonly struct InventoryItemPresentation
{
    public InventoryItemPresentation(bool networkManaged, bool foreignOwned,
        bool showForeignStyle, bool allowRecall, InventoryRecallRoute recallRoute)
    {
        NetworkManaged = networkManaged;
        ForeignOwned = foreignOwned;
        ShowForeignStyle = showForeignStyle;
        AllowRecall = allowRecall;
        RecallRoute = recallRoute;
    }

    public bool NetworkManaged { get; }
    public bool ForeignOwned { get; }
    public bool ShowForeignStyle { get; }
    public bool AllowRecall { get; }
    public InventoryRecallRoute RecallRoute { get; }
}

public static class InventoryPresentationPlanner
{
    public static bool ShouldPurgeForeignDroppedClaim(InventoryPresentationInput input,
        bool localSlotDropped, bool localSlotReservedOrLocked, bool localPossessionActive) =>
        localSlotDropped && localSlotReservedOrLocked && !localPossessionActive &&
        Plan(input).ForeignOwned;

    public static InventoryItemPresentation Plan(InventoryPresentationInput input)
    {
        bool managed = input.NetId != 0 && input.PersistentOwnerPlayerId != 0;
        if (!managed || input.LocalPlayerId == 0)
        {
            return new InventoryItemPresentation(managed, false, false, true,
                InventoryRecallRoute.Vanilla);
        }

        bool ownerMismatch = input.PersistentOwnerPlayerId != input.LocalPlayerId;
        bool recallable = input.RetrievalEligible && input.HasRetrievalClaim;
        bool foreign = recallable && ownerMismatch;
        if (ownerMismatch)
        {
            return new InventoryItemPresentation(true, foreign, foreign, false,
                InventoryRecallRoute.BlockForeignOwner);
        }

        if (!recallable)
            return new InventoryItemPresentation(true, false, false, false,
                InventoryRecallRoute.BlockNotRecallable);

        return new InventoryItemPresentation(true, false, false, true,
            input.IsLostAndFound
                ? InventoryRecallRoute.LostAndFoundRetrieval
                : InventoryRecallRoute.NetworkRecall);
    }
}
