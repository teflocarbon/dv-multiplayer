using Multiplayer.Core.Items;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Client.Items;

internal sealed partial class ClientItemManager
{
    // Binding the host's scene-authored items to this game's own copies of them, by stable key. The
    // catalogue itself is the Shared manager's: the server builds it too.

    /// <summary>The server's answer to this game's authored catalogue summary.</summary>
    internal void SetAuthoredCatalogueNegotiationResult(bool accepted)
    {
        authoredCatalogueAccepted = accepted;
    }

    public void DetachAuthoredProjectionForPossession(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (item == null || snapshot == null ||
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory) ||
            !NetworkedItemManager.IsEligibleAuthoredOfficeProp(item))
            return;
        string key = item.DetachAuthoredItemKey();
        if (WorldItemStableIdentity.IsValid(key))
        {
            shell.RemoveAuthoredItemProjection(key, item);
            ClientDetachedAuthoredSlots.Add(key);
        }
    }

    private bool TryBindAuthoredClientProjection(ItemUpdateData snapshot, bool acknowledge = true)
    {
        shell.EnsureAuthoredCatalogue();
        if (!authoredCatalogueAccepted)
        {
            DebugRuntime.Publish("item-world", "world-item.authored-binding-quarantined", DebugRuntimeSide.Client,
                DebugSeverity.Error, "Item", snapshot.ItemNetId.ToString(), new()
                {
                    ["authoredItemKey"] = snapshot.AuthoredItemKey,
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty,
                    ["reason"] = "catalogue-not-accepted"
                });
            return false;
        }
        if (!shell.TryGetAuthoredItem(snapshot.AuthoredItemKey, out NetworkedItem authored) || authored == null)
        {
            // A replenishable authored slot deliberately loses its original local scene
            // projection when that object becomes a persistent carried item. Only a key
            // recorded by that local transition may create a fresh projection; every other
            // missing authored key remains an integrity failure.
            if (ClientDetachedAuthoredSlots.Contains(snapshot.AuthoredItemKey) &&
                CreateItem(snapshot, false) &&
                NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem replacement) &&
                replacement != null)
            {
                ClientDetachedAuthoredSlots.Remove(snapshot.AuthoredItemKey);
                replacement.AdoptAuthoredItemKey(snapshot.AuthoredItemKey,
                    replenishableOfficeSlot: true);
                if (acknowledge)
                    ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
                        snapshot.AuthorityRevision, true);
                DebugRuntime.Publish("item-world", "world-item.authored-slot-replacement-projected",
                    DebugRuntimeSide.Client, entityType: "Item",
                    entityId: snapshot.ItemNetId.ToString(), data: new()
                    {
                        ["authoredItemKey"] = snapshot.AuthoredItemKey,
                        ["prefabName"] = snapshot.PrefabName ?? string.Empty
                    });
                return true;
            }
            DebugRuntime.Publish("item-world", "world-item.authored-binding-missing", DebugRuntimeSide.Client,
                DebugSeverity.Error, "Item", snapshot.ItemNetId.ToString(), new()
                {
                    ["authoredItemKey"] = snapshot.AuthoredItemKey,
                    ["prefabName"] = snapshot.PrefabName ?? string.Empty,
                    ["catalogueDigest"] = shell.AuthoredCatalogueDigest()
                });
            return false;
        }

        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem collision) && collision != null && collision != authored)
            RetireClientProjection(collision, "authored-binding-collision");
        DormantAuthoredClientProjections.Remove(authored);
        if (authored.NetId != 0 && authored.NetId != snapshot.ItemNetId)
            authored.Client()?.ResetNetworkLifetime();
        authored.NetId = snapshot.ItemNetId;
        authored.Client()?.SetNetworkBinding(true);
        authored.gameObject.SetActive(true);
        authored.ReceiveSnapshot(snapshot);
        if (acknowledge)
            ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
                snapshot.AuthorityRevision, true);
        DebugRuntime.Publish("item-world", "world-item.authored-projection-bound", DebugRuntimeSide.Client,
            entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: new()
            {
                ["authoredItemKey"] = snapshot.AuthoredItemKey,
                ["prefabName"] = snapshot.PrefabName ?? string.Empty
            });
        return true;
    }
}
