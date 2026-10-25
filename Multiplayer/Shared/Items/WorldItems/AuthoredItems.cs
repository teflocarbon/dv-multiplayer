using System.Linq;
using DV.Booklets;
using DV.Customization.Gadgets;
using DV.Items;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items.WorldItems;

/// <summary>
/// The scene-authored items this game has, by stable key (<see cref="AuthoredWorldItemCatalogue"/>),
/// for both roles: the server registers them in its interest index, and a remote client binds the
/// host's authored items to its own copies (ClientItemManager's AuthoredProjections). Its summary is
/// what the two compare on join (docs/design/items/world-items.md, "The authored catalogue").
/// </summary>
internal sealed class AuthoredItems
{
    private readonly NetworkedItemManager manager;
    private bool built;

    internal AuthoredItems(NetworkedItemManager manager) => this.manager = manager;

    /// <summary>The catalogue itself; dev tools rebuild it.</summary>
    internal AuthoredWorldItemCatalogue Catalogue { get; } = new();

    /// <summary>Builds the catalogue the first time it's needed; the server registers every item.</summary>
    internal void Ensure()
    {
        if (built)
            return;
        Catalogue.Rebuild();
        built = true;
        if (manager.ServerPart is { } server)
        {
            foreach (NetworkedItem item in NetworkedItem.GetAll().Where(value => value != null).Distinct().ToArray())
                server.RegisterHostWorldItem(item);
        }
    }

    internal (int Count, string[] Collisions, string Digest) Summary()
    {
        Ensure();
        return (Catalogue.BaselineCount, Catalogue.BaselineCollisionKeys, Catalogue.BaselineDigest);
    }

    internal bool TryGet(string key, out NetworkedItem item)
    {
        Ensure();
        return Catalogue.TryGet(key, out item);
    }

    /// <summary>A new object takes an authored slot; the one there before is retired.</summary>
    internal void Replace(string key, NetworkedItem item)
    {
        Ensure();
        if (Catalogue.TryGet(key, out NetworkedItem existing) && existing != null && existing != item)
        {
            manager.ServerPart?.UnregisterHostWorldItem(existing);
            existing.NoteRemovalCause(new ChangeCause<ItemRemovalReason>(ItemRemovalReason.AuthoredSlotReplaced,
                tick: NetworkLifecycle.Instance?.Tick ?? 0, detail: $"slot {key} now netId {item?.NetId ?? 0}"));
            existing.BeginColdStorageRetirement();
            manager.ServerPart?.ForgetRecord(existing.NetId);
            UnityEngine.Object.Destroy(existing.gameObject);
        }
        Catalogue.Replace(key, item);
    }

    internal string Digest() => Catalogue.Digest();

    internal void Remove(string key, NetworkedItem expected) => Catalogue.Remove(key, expected);

    /// <summary>
    /// A scene-authored office prop whose slot the host refills once it's taken away
    /// (OfficeStockReplenishment); a client lets go of the slot when it takes one.
    /// </summary>
    internal static bool IsEligibleOfficeProp(NetworkedItem item)
    {
        if (item?.Item?.InventorySpecs == null || !item.IsSceneAuthored ||
            item.Item.InventorySpecs.BelongsToPlayer || item.Item.InventorySpecs.IsEssential || item.Item.IsSnapped ||
            item.GetComponent<ItemContainer>() != null || item.GetComponent<GadgetItem>() != null ||
            item.GetComponent<JobBooklet>() != null || item.GetComponent<JobOverview>() != null ||
            item.GetComponent<JobReport>() != null)
            return false;
        return item.IsReplenishableAuthoredOfficeSlot && !item.IsAuthoredShopSlot;
    }
}
