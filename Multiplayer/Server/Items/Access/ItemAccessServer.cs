using System;
using Multiplayer.Core.Items.Access;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;

namespace Multiplayer.Server.Items.Access;

/// <summary>
/// The server's side of <see cref="ItemAccess"/>: it works out the facts for a player's request on
/// an item and asks the policy (docs/design/items/access.md). Called for every action: a stash, a
/// deposit, a recall, a retrieval, and a take from the world or from another player.
/// </summary>
public static class ItemAccessServer
{
    /// <summary>The station fixtures (owned-items.md, "Fixtures"). Empty until the shops work fills it.</summary>
    public static readonly ItemFixtureList Fixtures = new();

    public static bool IsFixture(NetworkedItem item, LiveItems.LiveItem record) =>
        Fixtures.Count > 0 && Fixtures.Contains(record?.PrefabName ?? item?.Item?.InventorySpecs?.ItemPrefabName,
            record?.AuthoredItemKey ?? item?.AuthoredItemKey);

    /// <summary>Whether the player may do this with the item; the reason when not.</summary>
    public static bool Allows(ServerPlayer actor, ItemAccessAction action, NetworkedItem item,
        LiveItems.LiveItem record, out string reason)
    {
        ItemAccessDecision decision = ItemAccess.Decide(new ItemAccessRequest(action,
            actorOwnsItem: actor != null && record != null && record.Owner != Guid.Empty &&
                           record.Owner == actor.Guid,
            isFixture: IsFixture(item, record),
            isEssential: item?.Item?.InventorySpecs?.IsEssential == true));
        reason = decision.Reason;
        return decision.Allowed;
    }
}
