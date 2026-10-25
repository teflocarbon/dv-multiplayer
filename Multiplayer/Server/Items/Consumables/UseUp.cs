using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Customization.Paint;
using DV.Interaction;
using HarmonyLib;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Server.Items.Consumables;

/// <summary>
/// A consumable used up is one server step (OI-VAN-03; docs/design/items/vanilla-storage.md, "Used
/// up"): the server destroys the used item, its end <c>UsedUp</c> by the player who used it, and makes
/// what replaces it, born <c>Replaced</c> (<see cref="ServerLedger.RecordReplacement"/>). A client asks
/// (<see cref="ItemAction.UseUp"/>); the host's own player's use comes here directly
/// (<see cref="UseUpPatch"/>). A garage key opens its padlock and goes; a spray can's paint is
/// sprayed and its empty can takes its place in the sprayer.
/// </summary>
internal static class UseUp
{
    // How far from a player the padlock their key opens may be: vanilla's reach, with room for lag.
    private const float PadlockReach = 6f;

    // The cans whose empty can is being made, so a repeated request doesn't spend one twice.
    private static readonly HashSet<ushort> spending = new();

    /// <summary>A player's request: empty when it is used up, else why not.</summary>
    internal static string Carry(ServerPlayer player, NetworkedItem item, NetworkedItem tool)
    {
        if (!LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record))
            return "item-unknown";
        if (item.TryGetComponent(out PadlockKey key))
        {
            if (record.Placement != ItemPlacementKind.PlayerHand || record.Holder != player.Guid)
                return "key-not-in-hand";
            Padlock padlock = PadlockFor(key, player.WorldPosition);
            return padlock == null ? "no-padlock-near" : Key(player, item, key, padlock);
        }
        if (item.TryGetComponent(out PaintCan can) && tool != null && tool.TryGetComponent(out PaintSprayer sprayer))
        {
            if (!LiveItems.TryGet(tool.NetId, out LiveItems.LiveItem toolRecord) ||
                toolRecord.Placement != ItemPlacementKind.PlayerHand || toolRecord.Holder != player.Guid)
                return "sprayer-not-in-hand";
            if (record.Placement != ItemPlacementKind.Magazine || record.MagazineNetId != tool.NetId)
                return "can-not-in-sprayer";
            return Can(player, item, can, sprayer);
        }
        return "not-a-consumable";
    }

    /// <summary>
    /// A key opens its padlock: it goes, used up, and the padlock opens on the server, whose garage
    /// unlock every game hears (<c>ClientboundGarageUnlockPacket</c>).
    /// </summary>
    internal static string Key(ServerPlayer player, NetworkedItem item, PadlockKey key, Padlock padlock)
    {
        if (!padlock.IsLocked || padlock.unlockingKey != key.keyType)
            return "padlock-not-this-keys";
        Multiplayer.Log($"UseUp: {player?.Username ?? "the server"} opened a {key.keyType} padlock with {item.name} (netId {item.NetId})");
        item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.UsedUp, player, "opened its padlock"));
        // In this game's own hand (the host's player), vanilla's unlock lets go of it first.
        if (key.Item != null && key.Item.IsGrabbed() && padlock.TryToUnlock(key))
            return string.Empty;
        key.KeyUsed();
        Object.Destroy(item.gameObject); // NetworkedItem.OnDestroy replicates the destroy
        padlock.ContinueUnlockingPadlock();
        // Far from the host's own player (or on a dedicated host) the padlock can be inactive, and plays
        // no animation to finish its unlock: it is finished here, so the garage opens all the same (K2).
        if (!padlock.isActiveAndEnabled)
            padlock.FinalizeUnlocking();
        return string.Empty;
    }

    /// <summary>
    /// A spray can's paint is sprayed (the paint itself syncs as any car's paint does): the can goes,
    /// used up, and its empty can is made in the sprayer's magazine, the can's replacement.
    /// </summary>
    internal static string Can(ServerPlayer player, NetworkedItem item, PaintCan can, PaintSprayer sprayer)
    {
        if (can.theme == null)
            return "can-spent";
        if (sprayer.magazine == null || sprayer.magazine[0] != item.gameObject)
            return "can-not-in-sprayer";
        if (!spending.Add(item.NetId))
            return "can-being-used-up";
        NetworkedItemManager.Instance.StartCoroutine(SpendCan(player, item, can, sprayer));
        return string.Empty;
    }

    private static IEnumerator SpendCan(ServerPlayer player, NetworkedItem item, PaintCan can, PaintSprayer sprayer)
    {
        ushort netId = item.NetId;
        try
        {
            GameObject empty = null;
            if (can.emptyCanPrefab != null)
            {
                Transform anchor = sprayer.unloadAnchor != null ? sprayer.unloadAnchor : sprayer.transform;
                empty = Object.Instantiate(can.emptyCanPrefab, anchor.position, anchor.rotation);
                if (empty.TryGetComponent(out RespawnOnDrop respawn))
                {
                    respawn.respawnOnDropThroughFloor = false;
                    respawn.ignoreDistanceFromSpawnPosition = true;
                }
                if (!empty.TryGetComponent(out NetworkedItem _))
                    empty.AddComponent<NetworkedItem>();
                yield return null; // its net id is assigned as it starts
            }
            if (item == null || sprayer == null)
            {
                Multiplayer.LogWarning($"UseUp: spray can {netId} or its sprayer went before its empty can was made");
                if (empty != null)
                    Object.Destroy(empty);
                yield break;
            }
            NetworkedItem replacement = empty != null ? empty.GetComponent<NetworkedItem>() : null;
            // Before the can goes: its entry ends replaced by the empty can, its owner's (an unowned can's is nobody's).
            if (replacement != null && !ServerLedger.RecordReplacement(item, replacement, ItemRemovalReason.UsedUp,
                    $"the empty can of {item.name}, sprayed by {player?.Username ?? "the server"}"))
                Multiplayer.LogDebug(() => $"UseUp: spray can {netId} has no live entry; its empty can is nobody's");
            Multiplayer.Log($"UseUp: {player?.Username ?? "the server"} sprayed {item.name} (netId {netId}); " +
                            (replacement != null ? $"its empty can is item {replacement.NetId}" : "it leaves nothing"));
            item.NoteRemovalCause(ItemChangeLog.Removal(ItemRemovalReason.UsedUp, player, "its paint sprayed"));
            sprayer.ignoreMagazineChange = true;
            sprayer.magazine.RemoveItem(item.gameObject, false, false);
            sprayer.ignoreMagazineChange = false;
            Object.Destroy(item.gameObject); // NetworkedItem.OnDestroy replicates the destroy
            sprayer.insertedCan = null;
            if (empty == null)
                yield break;
            sprayer.ignoreMagazineChange = true;
            bool loaded = sprayer.magazine.AddItem(empty, 0);
            sprayer.ignoreMagazineChange = false;
            if (!loaded)
            {
                Multiplayer.LogWarning($"UseUp: the empty can {replacement?.NetId} couldn't go into its sprayer; it lies beside it");
                yield break;
            }
            sprayer.insertedCan = empty.GetComponent<PaintCan>();
            if (sprayer.paintSprayerEffects != null)
            {
                sprayer.paintSprayerEffects.OnInserted(sprayer.insertedCan, false);
                sprayer.paintSprayerEffects.OnSpent();
            }
        }
        finally
        {
            spending.Remove(netId);
        }
    }

    public static void Clear() => spending.Clear();

    // Every padlock in a loaded scene, active or not: far from the host's own player, or on a
    // dedicated host, a garage's can be inactive, and FindObjectsOfType sees active ones only (K2).
    private static Padlock PadlockFor(PadlockKey key, Vector3 near) =>
        Resources.FindObjectsOfTypeAll<Padlock>()
            .Where(padlock => padlock != null && padlock.gameObject.scene.isLoaded &&
                              padlock.IsLocked && padlock.unlockingKey == key.keyType &&
                              Vector3.Distance(padlock.transform.position, near) <= PadlockReach)
            .OrderBy(padlock => Vector3.Distance(padlock.transform.position, near))
            .FirstOrDefault();
}

/// <summary>
/// The host's own player's key and sprayer (<see cref="UseUp"/>): the same server step a client's
/// request takes. A client's are <c>Client/Items/Behaviours/UseUpRequestPatch</c>.
/// </summary>
[HarmonyPatch]
internal static class UseUpPatch
{
    private static bool Serving => NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsSessionRunning &&
                                   NetworkLifecycle.Instance.IsHost();

    [HarmonyPatch(typeof(KeyUse), nameof(KeyUse.HandleUse))]
    [HarmonyPrefix]
    private static bool HandleUse(KeyUse __instance, ItemUseTarget target, ref bool __result)
    {
        if (!Serving || !__instance.TryGetComponent(out NetworkedItem item) || item.NetId == 0 ||
            !__instance.TryGetComponent(out PadlockKey key))
            return true;
        Padlock padlock = target != null ? target.GetComponent<Padlock>() : null;
        __result = padlock != null;
        string refused = padlock != null ? UseUp.Key(NetworkServer.Instance.LocalPlayer, item, key, padlock) : string.Empty;
        if (refused.Length > 0)
            Multiplayer.LogWarning($"UseUp: the host player's {item.name} (netId {item.NetId}) wasn't used: {refused}");
        return false;
    }

    [HarmonyPatch(typeof(PaintSprayer), nameof(PaintSprayer.Apply))]
    [HarmonyPrefix]
    private static bool Apply(PaintSprayer __instance, TrainCarPaint target)
    {
        PaintCan can = __instance.insertedCan;
        if (!Serving || can == null || target == null || !can.TryGetComponent(out NetworkedItem item) || item.NetId == 0)
            return true;
        target.CurrentTheme = can.theme;
        __instance.UnUse();
        string refused = UseUp.Can(NetworkServer.Instance.LocalPlayer, item, can, __instance);
        if (refused.Length > 0)
            Multiplayer.LogWarning($"UseUp: the host player's {item.name} (netId {item.NetId}) wasn't used up: {refused}");
        return false;
    }
}
