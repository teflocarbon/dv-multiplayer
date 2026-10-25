using System.Collections.Generic;
using Multiplayer.Shared.Items.Physics;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Client.Items.Physics;

/// <summary>
/// The client's half of the fall rescue (<see cref="ItemFallRescue"/>): it reports items near this
/// player that sank below its terrain, since the server usually has no terrain loaded around a
/// distant player. Loose items anywhere count, in a train or on a shelf as well as in the world:
/// the server scans, takes the reports and rescues (FallRescueServer).
/// </summary>
internal sealed class FallRescueClient
{

    private const float ClientReportCooldown = 5f;
    private const float ClientScanRadius = 200f;

    private float nextRescueScanAt;
    private readonly Dictionary<ushort, float> clientRescueReportedAt = new();

    internal void Run()
    {
        if (Time.unscaledTime < nextRescueScanAt)
            return;
        nextRescueScanAt = Time.unscaledTime + ItemFallRescue.RescueScanInterval;

        ClientScanForFallenItems();
    }

    private void ClientScanForFallenItems()
    {
        if (PlayerManager.PlayerTransform == null)
            return;
        Vector3 player = PlayerManager.PlayerTransform.position;
        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            if (item == null || item.NetId == 0 || !item.gameObject.activeInHierarchy ||
                item.Item == null || item.Item.IsGrabbed() ||
                (item.transform.position - player).sqrMagnitude > ClientScanRadius * ClientScanRadius)
                continue;
            if (item.GetItemState() is not (ItemState.Dropped or ItemState.Thrown))
                continue;
            if (ItemFallRescue.BelowTerrain(item.transform.position, out float surfaceY) is not float depth ||
                depth <= ItemFallRescue.BelowTerrainTolerance ||
                ItemFallRescue.HasFloorUnder(item))
                continue;
            if (clientRescueReportedAt.TryGetValue(item.NetId, out float reportedAt) &&
                Time.unscaledTime - reportedAt < ClientReportCooldown)
                continue;
            clientRescueReportedAt[item.NetId] = Time.unscaledTime;
            ItemClient.Current.SendItemRequest(new ItemRequest
            {
                ItemNetId = item.NetId,
                ExpectedRevision = item.AuthorityRevision,
                Action = ItemAction.ReportBelowMap,
                SurfaceHeight = surfaceY
            });
        }
    }
}
