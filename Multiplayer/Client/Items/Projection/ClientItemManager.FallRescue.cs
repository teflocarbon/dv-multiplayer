using System.Collections.Generic;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Client.Items.Projection;

internal sealed partial class ClientItemManager
{
    // The client's half of the fall rescue (NetworkedItemManager.FallRescue.cs): it reports items
    // near it that sink below its terrain, since the host usually has no terrain loaded around a
    // distant player. The host scans, takes the reports and rescues (ServerItemManager.FallRescue.cs).

    private const float ClientReportCooldown = 5f;
    private const float ClientScanRadius = 200f;

    private float nextRescueScanAt;
    private readonly Dictionary<ushort, float> clientRescueReportedAt = new();

    private void RunFallRescue()
    {
        if (Time.unscaledTime < nextRescueScanAt)
            return;
        nextRescueScanAt = Time.unscaledTime + NetworkedItemManager.RescueScanInterval;

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
            if (NetworkedItemManager.BelowTerrain(item.transform.position, out float surfaceY) is not float depth ||
                depth <= NetworkedItemManager.BelowTerrainTolerance ||
                NetworkedItemManager.HasFloorUnder(item))
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
