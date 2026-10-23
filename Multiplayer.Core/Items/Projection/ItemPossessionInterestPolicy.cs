using System;

namespace Multiplayer.Core.Items.Projection;

/// <summary>Recipient relevance for an item currently carried by a player.</summary>
public static class ItemPossessionInterestPolicy
{
    public static bool IsRelevant(bool ownerHasRecallClaim, Guid holder,
        Guid recipient, bool holderNearby) =>
        ownerHasRecallClaim ||
        holder != Guid.Empty && holder == recipient ||
        holderNearby;
}
