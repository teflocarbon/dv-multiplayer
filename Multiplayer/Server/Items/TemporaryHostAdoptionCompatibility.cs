using System;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Server.Items;

/// <summary>
/// TEMPORARY: the host's half of the client adoption compatibility bridge
/// (<c>Client/Items/TemporaryClientAdoptionCompatibility</c>): which adoption requests it accepts.
/// Delete both, with the adoption packets and coordinators, once every Derail Valley item producer
/// uses an explicit host-authoritative create operation.
/// </summary>
internal static class TemporaryHostAdoptionCompatibility
{
    internal static bool AllowsHostRequest(ItemAdoptionRequestData request,
        Func<string, bool> isJobDocumentPrefab, out string reason)
    {
        if (request.Snapshot == null)
        {
            reason = "missing-adoption-snapshot";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(request.AuthoredItemKey))
        {
            reason = "authored-item-adoption-forbidden";
            return false;
        }
        if (isJobDocumentPrefab?.Invoke(request.PrefabName) == true)
        {
            reason = "job-item-adoption-forbidden";
            return false;
        }
        if (!request.PlayerProperty)
        {
            reason = "temporary-adoption-requires-player-property";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
