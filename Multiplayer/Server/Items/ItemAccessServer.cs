using Multiplayer.Core.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;

namespace Multiplayer.Server.Items;

/// <summary>
/// The server's side of <see cref="ItemAccess"/>: it works out the facts for a player's request on
/// an item and asks the policy (owned-items.md, "Permissions", PR 1c). Called today where a player
/// stashes an item and where they deposit one; the other actions are wired by the PRs that change
/// them.
/// </summary>
public static class ItemAccessServer
{
    /// <summary>The station fixtures (owned-items.md, "Fixtures"). Empty until the shops work fills it.</summary>
    public static readonly ItemFixtureList Fixtures = new();

    public static bool IsFixture(NetworkedItem item, AuthoritativeItemRegistry.Record record) =>
        Fixtures.Count > 0 && Fixtures.Contains(record?.PrefabName ?? item?.Item?.InventorySpecs?.ItemPrefabName,
            record?.AuthoredItemKey ?? item?.AuthoredItemKey);

    /// <summary>Whether the player may do this with the item; the reason when not.</summary>
    public static bool Allows(ServerPlayer actor, ItemAccessAction action, NetworkedItem item,
        AuthoritativeItemRegistry.Record record, out string reason)
    {
        ItemAccessDecision decision = ItemAccess.Decide(new ItemAccessRequest(action,
            actorOwnsItem: actor != null && record != null && record.PersistentOwnerPlayerId != 0 &&
                           record.PersistentOwnerPlayerId == actor.PlayerId,
            isFixture: IsFixture(item, record),
            isEssential: item?.Item?.InventorySpecs?.IsEssential == true));
        reason = decision.Reason;
        return decision.Allowed;
    }
}
