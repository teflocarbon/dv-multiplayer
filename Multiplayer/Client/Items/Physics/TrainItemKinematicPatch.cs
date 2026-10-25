using DV.CabControls;
using DV.Items;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Client.Items.Physics;

/// <summary>
/// An item settled in a car stays kinematic with it on a client (OI-PHY-06; physics.md, "Loose items
/// on trains"). Vanilla's car lets its items go as its physics LOD comes down to 0
/// (<c>TrainItemActivityHandler.ToggleKinematic</c>): a rejoined client was given the item held, its
/// player came by the car a few seconds later, and the item was a live body there while every other
/// game held it (late-join-train-item@dedicated). One this game has settled in a car, with no lease
/// on it and not in a hand, is held again.
/// </summary>
[HarmonyPatch(typeof(TrainItemActivityHandler), nameof(TrainItemActivityHandler.ToggleKinematic))]
internal static class TrainItemKinematicPatch
{
    private static void Postfix(TrainItemActivityHandler __instance, bool kinematic)
    {
        if (kinematic || NetworkLifecycle.Instance == null || NetworkLifecycle.Instance.IsHost() ||
            NetworkedItemManager.Instance?.Spatial is not { } spatial || __instance.registeredItems == null)
            return;
        foreach (ItemBase registered in __instance.registeredItems)
        {
            if (registered == null || registered.IsSnapped || registered.IsGrabbed() ||
                !registered.TryGetComponent(out NetworkedItem item) ||
                NetworkedItemManager.Instance.HasActiveSpatialState(item.NetId) ||
                !spatial.TryGetCommitted(item.NetId, out ItemSpatialStateData settled) ||
                settled.Phase != ItemSpatialPhase.Settled || settled.WorldParentKind != ItemWorldParentKind.TrainInterior ||
                settled.AuthorityRevision != item.AuthorityRevision ||
                registered.ItemRigidbody is not { } body)
                continue;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }
    }
}
