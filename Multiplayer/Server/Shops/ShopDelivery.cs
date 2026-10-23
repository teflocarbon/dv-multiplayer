using Multiplayer.Server.Items.Ledger;
using Multiplayer.Core.Items.Ledger;
using System;
using System.Collections;
using DV.CabControls;
using DV.Shops;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Utils;
using UnityEngine;
using ItemMoveReason = Multiplayer.Core.Items.ChangeCauses.ItemMoveReason;

namespace Multiplayer.Server.Shops;

/// <summary>Puts bought items on the shop's counter for their owner (docs/design/shops.md, "Delivery").</summary>
public static class ShopDelivery
{
    // As GlobalShopController.InstantiatePurchasedItems, but always at the shop: that one leaves
    // items in the Lost and Found when the host is not near the shop. The buyer owns what they
    // bought; unowned, it went to whoever picked it up first.
    public static IEnumerator Spawn(string itemPrefabName, Shop shop, int amount, ServerPlayer buyer)
    {
        for (int i = 0; i < amount; i++)
        {
            if (shop == null)
                yield break;
            GameObject obj = UnityEngine.Object.Instantiate(Resources.Load(itemPrefabName) as GameObject,
                shop.itemSpawnTransform.position, Quaternion.identity);
            obj.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
            if (obj.TryGetComponent(out ShopRestocker restocker))
                restocker.restockOnItemDestroyed = true;
            ItemBase item = obj.GetComponent<ItemBase>();
            StorageController.Instance.AddItemToWorldStorage(item);
            item.ItemRigidbody.velocity = Vector3.zero;
            item.ItemRigidbody.angularVelocity = Vector3.zero;
            if (obj.TryGetComponent(out APurchaseTrigger trigger))
                trigger.OnPurchased(obj);
            NetworkedItem networked = obj.GetOrAddComponent<NetworkedItem>();
            if (buyer != null && buyer.Guid != Guid.Empty && networked != null && networked.NetId != 0 &&
                LiveItems.Ensure(networked) is { } record && record.Owner == Guid.Empty)
            {
                string detail = $"at {shop.name}, {i + 1} of {amount}, owner {buyer.Username}, lying in the world";
                string refused = ServerLedger.Own(record, buyer.Guid, BirthReason.Purchased, buyer.Guid, detail);
                if (refused.Length > 0)
                    Multiplayer.LogWarning($"Item {networked.name} (netId {networked.NetId}) bought by {buyer.Username}: the ledger refused it ({refused})");
                ItemChangeLog.RecordMove(networked, record, ItemChangeLog.Move(ItemMoveReason.Purchased, buyer, detail));
            }
            else if (networked != null && networked.NetId != 0)
                Multiplayer.LogWarning($"Item {networked.name} (netId {networked.NetId}) bought by " +
                    $"{buyer?.Username ?? "nobody"} at {shop.name} has no owner: it already had one, or the buyer has no identity or has left");
            yield return WaitFor.Seconds(0.5f);
        }
    }
}
