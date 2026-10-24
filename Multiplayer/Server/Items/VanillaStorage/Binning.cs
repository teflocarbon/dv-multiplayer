using System;
using System.Collections.Generic;
using DV.CabControls;
using HarmonyLib;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Server.Items.VanillaStorage;

/// <summary>
/// Bins and dumpsters (OI-VAN-02, D10; docs/design/items/vanilla-storage.md, "Bins"). Vanilla's
/// <c>ItemDumpster</c> takes an item in and its <c>RespawnOnDrop</c> destroys it once every player is
/// 75 m off. In multiplayer the server decides: a player may bin their own item or one nobody owns,
/// and its end is theirs, an owned one's entry ending <c>Discarded</c> (an essential is issued again
/// at its owner's next join, as one they have none of). Someone else's item is refused and put down
/// beside the bin. Clients never bin: their <c>RespawnOnDrop</c> doesn't run.
/// </summary>
internal static class Binning
{
    private readonly struct Binner
    {
        public Binner(Guid identity, byte playerId, string name)
        {
            Identity = identity;
            PlayerId = playerId;
            Name = name;
        }

        public Guid Identity { get; }
        public byte PlayerId { get; }
        public string Name { get; }
    }

    // The items in a bin, and who binned each.
    private static readonly Dictionary<ushort, Binner> binned = new();

    /// <summary>Whether the item is in a bin, waiting to go.</summary>
    internal static bool IsBinned(ushort itemNetId) => binned.ContainsKey(itemNetId);

    /// <summary>
    /// An item enters a bin: true if its binner may bin it (their own, or nobody's), and it is the
    /// bin's. Someone else's is refused and put down beside the bin.
    /// </summary>
    internal static bool TryBin(NetworkedItem item, ItemDumpster bin)
    {
        if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return true;
        Binner by = BinnerOf(record);
        if (record.Owner == Guid.Empty || record.Owner == by.Identity)
        {
            binned[item.NetId] = by;
            Multiplayer.Log($"Binning: {by.Name} put {record.PrefabName} (netId {item.NetId}) in a bin");
            return true;
        }
        Multiplayer.Log($"Binning: {by.Name} can't bin {ItemChangeLog.PlayerName(record.Owner)}'s {record.PrefabName} (netId {item.NetId}); it is put down beside the bin");
        // Still in a hand, it is just not the bin's; lying in the bin, it goes beside it.
        if (record.Placement == ItemPlacementKind.World)
            PutBeside(item, record, bin, by);
        return false;
    }

    /// <summary>The item left its bin (taken out, respawned, destroyed): it's no longer binned.</summary>
    internal static void Unbinned(ushort itemNetId) => binned.Remove(itemNetId);

    /// <summary>
    /// Whether vanilla's respawn or destroy may run for a networked item (OI-VAN-05): a binned item's
    /// destroy, and the reset to its spawn of a scene's own item nobody owns. Never a move into
    /// vanilla's Lost and Found list: an owned item's collection is the server's.
    /// </summary>
    internal static bool AllowsRespawnOrDestroy(NetworkedItem item, RespawnOnDrop respawner)
    {
        if (binned.ContainsKey(item.NetId))
            return !respawner.respawnOnDropThroughFloor;
        if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record) || record.Owner != Guid.Empty)
            return false;
        return respawner.respawnOnDropThroughFloor && item.IsSceneAuthored && item.Item != null && !item.Item.BelongsToPlayer();
    }

    /// <summary>
    /// Before each step of vanilla's respawn-or-destroy for a networked item: a binned item's destroy
    /// is its binner's, <c>Discarded</c>, so its entry ends with that.
    /// </summary>
    internal static void BeforeRespawnOrDestroyStep(NetworkedItem item)
    {
        if (!binned.TryGetValue(item.NetId, out Binner by))
            return;
        item.NoteRemovalCause(new ChangeCause<ItemRemovalReason>(ItemRemovalReason.Discarded, by.PlayerId, by.Name,
            NetworkLifecycle.Instance?.Tick ?? 0, "binned"));
    }

    public static void Clear() => binned.Clear();

    // Whoever put it there: its holder still, else the player whose move it was last.
    private static Binner BinnerOf(LiveItems.LiveItem record)
    {
        Guid identity = record.Holder != Guid.Empty ? record.Holder : PlayerNumbers.Identity(record.LastMove.PlayerId);
        ServerPlayer player = null;
        if (identity != Guid.Empty)
            PlayerNumbers.TryGetSession(identity, out player);
        return new Binner(identity, player?.PlayerId ?? 0,
            player?.Username ?? (identity != Guid.Empty ? ItemChangeLog.PlayerName(identity) : "nobody"));
    }

    // Beside the bin on the binner's side (else its front), on the ground: an ordinary world item again.
    private static void PutBeside(NetworkedItem item, LiveItems.LiveItem record, ItemDumpster bin, Binner by)
    {
        Bounds bounds = bin.TryGetComponent(out Collider trigger) ? trigger.bounds : new Bounds(bin.transform.position, Vector3.one * 2f);
        Vector3 toward = bin.transform.forward;
        if (by.PlayerId != 0 && NetworkServer.Instance.TryGetServerPlayer(by.PlayerId, out ServerPlayer player))
        {
            Vector3 flat = player.WorldPosition - bounds.center;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.01f)
                toward = flat.normalized;
        }
        toward.y = 0f;
        if (toward.sqrMagnitude < 0.01f)
            toward = Vector3.forward;
        float reach = Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.7f;
        Vector3 world = bounds.center + toward.normalized * reach;
        world.y = bounds.min.y + 0.3f;
        ServerItemManager.Instance?.PutAt(item, record, world - WorldMover.currentMove,
            ItemChangeLog.Move(ItemMoveReason.BinRefused,
                detail: $"{by.Name} can't bin {ItemChangeLog.PlayerName(record.Owner)}'s item"));
    }
}

/// <summary>The server's half of vanilla's <c>ItemDumpster</c> (<see cref="Binning"/>). Clients' bins stay vanilla's, and harmless: their items don't respawn or go.</summary>
[HarmonyPatch(typeof(ItemDumpster))]
internal static class ItemDumpsterPatch
{
    private static readonly Func<ItemDumpster, ItemBase, bool> isValid =
        AccessTools.MethodDelegate<Func<ItemDumpster, ItemBase, bool>>(AccessTools.Method(typeof(ItemDumpster), "IsValidDumpsterItem"));

    private static bool Serving => NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsSessionRunning &&
                                   NetworkLifecycle.Instance.IsHost();

    [HarmonyPatch("RegisterItem")]
    [HarmonyPrefix]
    private static bool BeforeRegister(ItemDumpster __instance, ItemBase itemBase)
    {
        if (!Serving || itemBase == null || !NetworkedItem.TryGetNetworkedItem(itemBase, out NetworkedItem item) ||
            item == null || item.NetId == 0 || !isValid(__instance, itemBase) || itemBase.itemDisabler == null)
            return true;
        return Binning.TryBin(item, __instance);
    }

    [HarmonyPatch("UnregisterItem")]
    [HarmonyPostfix]
    private static void AfterUnregister(ItemBase itemBase)
    {
        if (Serving && itemBase != null && NetworkedItem.TryGetNetworkedItem(itemBase, out NetworkedItem item) && item != null)
            Binning.Unbinned(item.NetId);
    }
}
