using Multiplayer.Core.Items.Ledger;

namespace Multiplayer.Core.Items.Inventory;

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
    public InventoryPresentationInput(ushort netId, ItemRelation relation,
        bool hasLocalPlayer, bool retrievalEligible, bool hasRetrievalClaim, bool isLostAndFound)
    {
        NetId = netId;
        Relation = relation;
        HasLocalPlayer = hasLocalPlayer;
        RetrievalEligible = retrievalEligible;
        HasRetrievalClaim = hasRetrievalClaim;
        IsLostAndFound = isLostAndFound;
    }

    public ushort NetId { get; }
    /// <summary>The local player's relation to the item, as the server told it.</summary>
    public ItemRelation Relation { get; }
    public bool HasLocalPlayer { get; }
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

    /// <summary>
    /// How an item shows in the local player's inventory. Red for every item someone else owns,
    /// online or not, whatever it is (D3); only the owner's own essentials with a claim recall.
    /// </summary>
    public static InventoryItemPresentation Plan(InventoryPresentationInput input)
    {
        bool managed = input.NetId != 0 && input.Relation.IsOwned();
        if (!managed || !input.HasLocalPlayer)
        {
            return new InventoryItemPresentation(managed, false, false, true,
                InventoryRecallRoute.Vanilla);
        }

        if (input.Relation == ItemRelation.SomeoneElses)
        {
            return new InventoryItemPresentation(true, true, true, false,
                InventoryRecallRoute.BlockForeignOwner);
        }

        bool recallable = input.RetrievalEligible && input.HasRetrievalClaim;
        if (!recallable)
            return new InventoryItemPresentation(true, false, false, false,
                InventoryRecallRoute.BlockNotRecallable);

        return new InventoryItemPresentation(true, false, false, true,
            input.IsLostAndFound
                ? InventoryRecallRoute.LostAndFoundRetrieval
                : InventoryRecallRoute.NetworkRecall);
    }
}
