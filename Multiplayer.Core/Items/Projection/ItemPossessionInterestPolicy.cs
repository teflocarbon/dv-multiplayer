namespace Multiplayer.Core.Items.Projection;

/// <summary>Recipient relevance for an item currently carried by a player.</summary>
public static class ItemPossessionInterestPolicy
{
    public static bool IsRelevant(bool ownerHasRecallClaim, byte placementPlayerId,
        byte recipientPlayerId, bool holderNearby) =>
        ownerHasRecallClaim ||
        placementPlayerId != 0 && placementPlayerId == recipientPlayerId ||
        holderNearby;
}
