using System;
using System.Collections;
using DV.CabControls;
using DV.Shops;
using Multiplayer.Server.Items;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Utils;
using UnityEngine;
using ItemMoveReason = Multiplayer.Core.Items.ItemMoveReason;

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
                AuthoritativeItemRegistry.Ensure(networked) is { PersistentOwnerPlayerId: 0 } record)
            {
                record.PersistentOwnerPlayerId = buyer.PlayerId;
                record.PersistentOwnerIdentity = buyer.Guid;
                buyer.AddOwnedItem(networked.NetId);
                ItemChangeLog.RecordMove(networked, record, ItemChangeLog.Move(ItemMoveReason.Purchased, buyer,
                    $"at {shop.name}, {i + 1} of {amount}, owner {buyer.Username}, lying in the world"));
            }
            else if (networked != null && networked.NetId != 0)
                Multiplayer.LogWarning($"Item {networked.name} (netId {networked.NetId}) bought by " +
                    $"{buyer?.Username ?? "nobody"} at {shop.name} has no owner: it already had one, or the buyer has no identity or has left");
            yield return WaitFor.Seconds(0.5f);
        }
    }
}
