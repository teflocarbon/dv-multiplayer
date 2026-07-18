using System;
using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// TEMPORARY compatibility bridge for Derail Valley creation paths that have not yet been
/// converted to explicit host-authoritative operations (for example, remaining shop outputs).
///
/// This is not a general authority mechanism. It must shrink as each producer becomes a
/// request/validation/host-create flow, then be deleted together with the adoption packets and
/// coordinators. Scene-authored objects, job-lifecycle objects, and objects not explicitly marked
/// as player property are never eligible.
/// </summary>
internal static class TemporaryClientAdoptionCompatibility
{
    internal const string RemovalCondition =
        "Remove after every runtime item producer uses an explicit host-authoritative create operation.";

    internal static bool AllowsLocalItem(NetworkedItem item,
        Func<NetworkedItem, bool> isSpecialLifecycleItem, out string reason)
    {
        if (item?.Item?.InventorySpecs == null)
        {
            reason = "missing-item-spec";
            return false;
        }
        if (item.IsSceneAuthored)
        {
            reason = "scene-authored-items-bind-by-stable-key";
            return false;
        }
        if (isSpecialLifecycleItem?.Invoke(item) == true)
        {
            reason = "special-lifecycle-item";
            return false;
        }
        if (!item.Item.InventorySpecs.BelongsToPlayer)
        {
            reason = "not-marked-as-player-property";
            return false;
        }

        reason = "temporary-unconverted-player-item-producer";
        return true;
    }

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
