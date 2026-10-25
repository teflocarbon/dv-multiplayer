using System;
using System.Collections.Generic;
using Multiplayer.Core.Items.Projection;
using Multiplayer.Client.Items.LiveState;
using Multiplayer.Client.Items.Projection;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.WorldItems;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Client.Items.WorldItems;

/// <summary>
/// Binds the host's scene-authored items to this game's own copies of them, by stable key. The
/// catalogue itself is the Shared manager's (<see cref="AuthoredItems"/>): the server builds it too.
/// </summary>
internal sealed class AuthoredProjections
{
    private readonly ClientItemManager manager;
    private readonly NetworkedItemManager shell;
    // Authored slots this game let go of when it took their prop; only these may get a fresh copy.
    private readonly HashSet<string> detachedSlots = new(StringComparer.Ordinal);
    private bool accepted;

    internal AuthoredProjections(ClientItemManager manager, NetworkedItemManager shell)
    {
        this.manager = manager;
        this.shell = shell;
    }

    /// <summary>The server's answer to this game's authored catalogue summary.</summary>
    internal void SetNegotiationResult(bool accepted) => this.accepted = accepted;

    internal void Clear() => detachedSlots.Clear();

    internal void DetachForPossession(NetworkedItem item, ItemUpdate snapshot)
    {
        if (item == null || snapshot == null ||
            snapshot.ItemState is not (ItemState.InHand or ItemState.InInventory or ItemState.InMagazine) ||
            !AuthoredItems.IsEligibleOfficeProp(item))
            return;
        string key = item.DetachAuthoredItemKey();
        if (WorldItemStableIdentity.IsValid(key))
        {
            shell.Authored.Remove(key, item);
            detachedSlots.Add(key);
        }
    }

    internal bool TryBind(ItemUpdate snapshot, bool acknowledge = true)
    {
        shell.Authored.Ensure();
        if (!accepted)
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
        if (!shell.Authored.TryGet(snapshot.AuthoredItemKey, out NetworkedItem authored) || authored == null)
        {
            // A replenishable authored slot deliberately loses its original local scene
            // projection when that object becomes a persistent carried item. Only a key
            // recorded by that local transition may create a fresh projection; every other
            // missing authored key remains an integrity failure.
            if (detachedSlots.Contains(snapshot.AuthoredItemKey) &&
                manager.CreateItem(snapshot, false) &&
                NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem replacement) &&
                replacement != null)
            {
                detachedSlots.Remove(snapshot.AuthoredItemKey);
                replacement.AdoptAuthoredItemKey(snapshot.AuthoredItemKey,
                    replenishableOfficeSlot: true);
                if (acknowledge)
                    ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
                        snapshot.AuthorityRevision, ProjectionAck.Projected);
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
                    ["catalogueDigest"] = shell.Authored.Digest()
                });
            return false;
        }

        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem collision) && collision != null && collision != authored)
            manager.RetireClientProjection(collision, "authored-binding-collision");
        manager.DormantAuthoredClientProjections.Remove(authored);
        if (authored.NetId != 0 && authored.NetId != snapshot.ItemNetId)
            authored.Client()?.ResetNetworkLifetime();
        authored.NetId = snapshot.ItemNetId;
        authored.Client()?.SetNetworkBinding(true);
        authored.gameObject.SetActive(true);
        authored.ReceiveSnapshot(snapshot);
        if (acknowledge)
            ItemClient.Current.SendWorldItemProjectionAck(snapshot.ItemNetId,
                snapshot.AuthorityRevision, ProjectionAck.Projected);
        DebugRuntime.Publish("item-world", "world-item.authored-projection-bound", DebugRuntimeSide.Client,
            entityType: "Item", entityId: snapshot.ItemNetId.ToString(), data: new()
            {
                ["authoredItemKey"] = snapshot.AuthoredItemKey,
                ["prefabName"] = snapshot.PrefabName ?? string.Empty
            });
        return true;
    }
}
