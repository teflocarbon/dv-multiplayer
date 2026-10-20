using System;
using Multiplayer.Shared.Items;

namespace Multiplayer.Client.Items;

/// <summary>
/// TEMPORARY compatibility bridge for Derail Valley creation paths that have not yet been
/// converted to explicit host-authoritative operations (for example, remaining shop outputs).
///
/// This is not a general authority mechanism. It must shrink as each producer becomes a
/// request/validation/host-create flow, then be deleted together with the adoption packets and
/// coordinators. Scene-authored objects, job-lifecycle objects, and objects not explicitly marked
/// as player property are never eligible.
///
/// This is the client's half: which items it may ask the host to adopt. The host's half, which
/// requests it accepts, is <c>Server/Items/TemporaryHostAdoptionCompatibility</c>.
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
}
