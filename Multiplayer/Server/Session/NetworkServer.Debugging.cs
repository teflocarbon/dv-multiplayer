using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Session;

// NetworkServer's debug data for the observability overlay (see docs/design/code-layout.md).
public partial class NetworkServer : IDebugServer
{
    public Dictionary<string, object>[] InterestMatrix(ushort itemNetId)
    {
        if (!NetworkedItem.TryGet(itemNetId, out NetworkedItem item) || item == null)
            return Array.Empty<Dictionary<string, object>>();
        return ServerPlayers.Select(player =>
        {
            bool nearby = player.NearbyItems.TryGetValue(item, out float nearbyTime);
            bool known = player.KnownItems.TryGetValue(item, out uint knownTick);
            float distance = Vector3.Distance(player.WorldPosition, item.transform.position);
            string action = player.LoadingState < PlayerLoadingState.ReadyForItems ? "not-ready" : !nearby ? "outside-interest" : !known ? "create-required" : knownTick < item.LastDirtyTick ? "full-sync-required" : "up-to-date";
            return new Dictionary<string, object>
            {
                ["playerId"] = player.PlayerId, ["player"] = player.Username, ["loadingState"] = player.LoadingState.ToString(),
                ["distance"] = distance, ["nearby"] = nearby, ["nearbyAgeSeconds"] = nearby ? Time.time - nearbyTime : 0,
                ["known"] = known, ["knownTick"] = knownTick, ["lastDirtyTick"] = item.LastDirtyTick, ["decision"] = action
            };
        }).ToArray();
    }
}
