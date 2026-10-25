using DV.CabControls;
using DV.Items;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using UnityEngine;

namespace Multiplayer.Shared.Items.Physics;

/// <summary>
/// An item settled in a car stays kinematic with it in every game (OI-PHY-06; train-wake.md, "Settled
/// train items"). Vanilla lets a car's items go when its physics LOD comes down to -1, not 0
/// (<c>TrainItemActivityHandler.ToggleItemsDelayed</c>: kinematic while the LOD is above -1). That's
/// the car the local player is in (<c>OnPlayerCarChanged</c>), a car with <c>LockHighestLOD</c>, or a
/// forced update at -1. A LOD above it frees too: one outside the car's bounding box goes live
/// (<c>ToggleKinematic(true)</c>). In late-join-train-item@dedicated the rejoined client got the mug
/// held, its player was put back in the cab a moment later, and the mug was a live body there while
/// every other game held it. After either toggle, an item this game has settled in the car, with no
/// lease on it and not in a hand, is held again. A player host's server copy is the same: its own
/// player boarding the car let it go with no lease. A dedicated server has no player, but a forced
/// LOD would free it the same way.
/// </summary>
[HarmonyPatch(typeof(TrainItemActivityHandler), nameof(TrainItemActivityHandler.ToggleKinematic))]
internal static class TrainItemKinematicPatch
{
    private static void Postfix(TrainItemActivityHandler __instance)
    {
        if (NetworkedItemManager.Instance?.Spatial is not { } spatial || __instance.registeredItems == null)
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
