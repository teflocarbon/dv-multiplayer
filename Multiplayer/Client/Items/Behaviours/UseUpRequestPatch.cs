using DV.Customization.Paint;
using DV.Interaction;
using HarmonyLib;
using Multiplayer.Client.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Items.Behaviours;

/// <summary>
/// A client's key and sprayer (OI-VAN-03; docs/design/items/vanilla-storage.md, "Used up"): what is
/// used up is the server's to destroy and replace, so the client asks (<see cref="ItemAction.UseUp"/>)
/// and vanilla's own destroy and replacement don't run here. The server's destroy, its empty can
/// and the garage it opens come back as every game hears them. The host's own player's use is the
/// server's <c>UseUpPatch</c>.
/// </summary>
[HarmonyPatch]
public static class UseUpRequestPatch
{
    private static bool Asking => NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsSessionRunning &&
                                  NetworkRole.IsRemoteClient;

    [HarmonyPatch(typeof(KeyUse), nameof(KeyUse.HandleUse))]
    [HarmonyPrefix]
    private static bool HandleUse(KeyUse __instance, ItemUseTarget target, ref bool __result)
    {
        if (!Asking)
            return true;
        Padlock padlock = target != null ? target.GetComponent<Padlock>() : null;
        __result = padlock != null;
        if (padlock != null && padlock.IsLocked && __instance.TryGetComponent(out PadlockKey key) &&
            padlock.unlockingKey == key.keyType && __instance.TryGetComponent(out NetworkedItem item) && item.NetId != 0)
            NetworkClient.Instance.SendItemRequest(new ItemRequest
            {
                ItemNetId = item.NetId,
                ExpectedRevision = item.AuthorityRevision,
                Action = ItemAction.UseUp
            });
        return false;
    }

    [HarmonyPatch(typeof(PaintSprayer), nameof(PaintSprayer.Apply))]
    [HarmonyPrefix]
    private static bool Apply(PaintSprayer __instance, TrainCarPaint target)
    {
        PaintCan can = __instance.insertedCan;
        if (!Asking || can == null || target == null || !can.TryGetComponent(out NetworkedItem item) || item.NetId == 0 ||
            !__instance.TryGetComponent(out NetworkedItem sprayer) || sprayer.NetId == 0)
            return true;
        // The paint syncs as any car's does; the can is the server's.
        target.CurrentTheme = can.theme;
        __instance.UnUse();
        NetworkClient.Instance.SendItemRequest(new ItemRequest
        {
            ItemNetId = item.NetId,
            ExpectedRevision = item.AuthorityRevision,
            Action = ItemAction.UseUp,
            MagazineNetId = sprayer.NetId
        });
        return false;
    }
}
