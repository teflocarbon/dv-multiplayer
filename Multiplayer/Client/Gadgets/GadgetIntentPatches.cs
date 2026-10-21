using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.Interaction;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Client.Session;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// A remote client's gadget installs and removals become requests where vanilla would commit them
/// (gadgets.md, "Requests"): <c>GadgetItem.TryPlace</c>, which every install path ends in, and the
/// hammer's, the hand's and the telegrab's removals. Until gadget PR 3b predicts them, nothing changes
/// in this game until the server's change log says so: the item stays in hand until the
/// <c>Installed</c> entry places it, and a removed gadget stays until the <c>Removed</c> entry. A gadget
/// taken off by hand is then picked up as an ordinary grab. Vanilla's own checks run first, so a request
/// is only sent for what vanilla would have done. Inside the apply scope, and in the host's game, they
/// pass straight through. Labels (an install that makes a new item) are gadget PR 9's.
/// </summary>
[HarmonyPatch]
internal static class GadgetIntentPatches
{
    private sealed class Pending
    {
        public NetworkedItem Item;
        public GadgetBase.GadgetRemovalMethod Method;
        // Who picks the item up once the removal is confirmed.
        public GadgetHandNonVR HandNonVR;
        public GadgetHandVR HandVR;
    }

    private static readonly Dictionary<uint, Pending> pending = new();
    private static uint nextOperation;
    // What TryPlaceOnHit hit, which TryPlace isn't told.
    private static Mount hitMount;

    internal static void Reset() => pending.Clear();

    private static bool Intercepts => NetworkRole.IsRemoteClient && !GadgetApplyScope.Active;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetItem), "TryPlaceOnHit")]
    private static void NoteHit(RaycastHitDV hit)
    {
        GadgetBase hitGadget = hit.collider != null ? hit.collider.GetComponentInParent<GadgetBase>() : null;
        hitMount = hitGadget != null ? hitGadget.GetComponent<Mount>() : null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetItem), nameof(GadgetItem.TryPlace))]
    private static bool TryPlace(Customization destination, Vector3 position, Quaternion rotation, GadgetItem gadgetItem,
        Collider colliderForPlacementData, bool instantiateGadget, ref GadgetBase gadget, ref bool __result)
    {
        if (!Intercepts || instantiateGadget)
            return true;
        gadget = null;
        __result = false;
        // Vanilla's first check, before anything is asked of the server.
        if (gadgetItem == null || !GadgetItem.CanPlace(position, rotation, gadgetItem.gadgetPrefab))
            return false;
        if (destination is not TrainCarCustomization { TrainCar: { } car } || !NetworkedTrainCar.TryGetNetId(car, out ushort carNetId))
            return false;   // vehicles only (GadgetVehiclesOnlyPatch says so in the aim text)
        NetworkedItem item = gadgetItem.GetComponent<NetworkedItem>();
        if (item == null || item.NetId == 0 || IsPending(item))
            return false;

        Transform frame = destination.GetParentingTransform();
        int mountHostUid = 0;
        if (hitMount != null && gadgetItem.gadgetPrefab.RequiredMountPoints > 0 &&
            hitMount.GetComponent<GadgetBase>() is { IsLinked: true } host && host.Custom == destination)
            mountHostUid = host.UID;
        GadgetColliderPath.TryDescribe(car, colliderForPlacementData, out string colliderPath, out byte colliderIndex);

        uint operation = ++nextOperation;
        pending[operation] = new Pending { Item = item };
        NetworkClient.Instance?.SendGadgetInstall(new ServerboundGadgetInstallPacket
        {
            OperationId = operation,
            ItemNetId = item.NetId,
            CarNetId = carNetId,
            LocalPosition = frame.InverseTransformPoint(position),
            LocalRotation = Quaternion.Inverse(frame.rotation) * rotation,
            MountHostUid = mountHostUid,
            ColliderPath = colliderPath,
            ColliderIndex = colliderIndex
        });
        return false;
    }

    // The hammer.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetRemover), "Remove")]
    private static bool HammerRemove(GadgetBase target, ref GadgetItem __result)
    {
        if (!Request(target, GadgetBase.GadgetRemovalMethod.Remover, null, null))
            return true;
        __result = null;
        return false;
    }

    // An empty hand, non-VR: vanilla removes it and makes the hand hold it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetHandNonVR), "TryGrab")]
    private static bool HandRemove(GadgetHandNonVR __instance, GadgetBase target, ref bool __result)
    {
        if (!Request(target, GadgetBase.GadgetRemovalMethod.EmptyHand, __instance, null))
            return true;
        __result = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetHandVR), "TryGrab")]
    private static bool HandRemoveVR(GadgetHandVR __instance, GadgetBase target, ref bool __result)
    {
        if (!Request(target, GadgetBase.GadgetRemovalMethod.EmptyHand, null, __instance))
            return true;
        __result = true;
        return false;
    }

    // A VR telegrab of an installed gadget pulls it off; the item lands where it was, to telegrab again.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetBase), nameof(GadgetBase.OnTelegrabbableChanged))]
    private static bool TelegrabRemove(GadgetBase __instance, bool isBeingTelegrabbed) =>
        !(isBeingTelegrabbed && __instance.IsLinked && Request(__instance, GadgetBase.GadgetRemovalMethod.EmptyHand, null, null));

    // While this game takes a gadget off, its mount points are freed as part of the removal, not by a player.
    private static int unlinking;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Unlink))]
    private static void UnlinkStart() => unlinking++;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Customization.CustomizerBase), nameof(Customization.CustomizerBase.Unlink))]
    private static void UnlinkEnd() => unlinking--;

    // The drill bolting, the tape taping and the hand untaping (PR 4). The tool in hand the server finds itself.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Drillable), nameof(Drillable.SetMountPointState))]
    private static bool MountPoint(Drillable __instance, int index, MountPoint.States newState)
    {
        if (!Intercepts || unlinking > 0 || __instance.ThisGadget is not { IsLinked: true } gadget ||
            !GadgetJoinApplier.IsServerSent(gadget) || index < 0 || index >= __instance.MountPointCount ||
            __instance.GetMountPointState(index) == newState)
            return true;
        NetworkedItem item = gadget.GadgetItem?.GetComponent<NetworkedItem>();
        if (item == null || item.NetId == 0)
            return true;
        uint operation = ++nextOperation;
        pending[operation] = new Pending();
        NetworkClient.Instance?.SendGadgetMountPoint(new ServerboundGadgetMountPointPacket
        {
            OperationId = operation,
            ItemNetId = item.NetId,
            Index = index,
            State = (byte)newState
        });
        return false;
    }

    // The tape counts down here as vanilla's does; the server counts too, and swapping an empty roll for
    // a new item is the server's (not done yet).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DuctTape), nameof(DuctTape.ConsumeOneUse))]
    private static bool TapeUse(DuctTape __instance)
    {
        if (!Intercepts)
            return true;
        if (__instance.usesLeft > 0)
        {
            __instance.usesLeft--;
            __instance.tapeModelUpdater?.UpdateActiveStates(__instance.PercentageUsesLeft);
        }
        return false;
    }

    // The crimping tool wiring two gadgets, or unwiring them (PR 5). A removal's unwiring isn't a player's.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetWiringModule.WireLinkPort), nameof(GadgetWiringModule.WireLinkPort.Wire))]
    private static bool Wire(GadgetWiringModule.WireLinkPort a, GadgetWiringModule.WireLinkPort b, ref bool __result) =>
        !WireRequest(a, b, true, ref __result);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetWiringModule.WireLinkPort), nameof(GadgetWiringModule.WireLinkPort.Unwire),
        new[] { typeof(GadgetWiringModule.WireLinkPort), typeof(GadgetWiringModule.WireLinkPort) })]
    private static bool Unwire(GadgetWiringModule.WireLinkPort a, GadgetWiringModule.WireLinkPort b, ref bool __result) =>
        !WireRequest(a, b, false, ref __result);

    private static bool WireRequest(GadgetWiringModule.WireLinkPort a, GadgetWiringModule.WireLinkPort b, bool wire, ref bool result)
    {
        if (!Intercepts || unlinking > 0 || a?.owner is not { IsLinked: true } source || b?.owner is not { IsLinked: true } target ||
            source.Custom != target.Custom || !GadgetJoinApplier.IsServerSent(source) || !GadgetJoinApplier.IsServerSent(target) ||
            source.Custom is not TrainCarCustomization { TrainCar: { } car } || !NetworkedTrainCar.TryGetNetId(car, out ushort carNetId))
            return false;
        result = false;
        uint operation = ++nextOperation;
        pending[operation] = new Pending();
        NetworkClient.Instance?.SendGadgetWire(new ServerboundGadgetWirePacket
        {
            OperationId = operation,
            CarNetId = carNetId,
            SourceUid = source.UID,
            SourcePort = source.wiring.wireLinkPorts.IndexOf(a),
            TargetUid = target.UID,
            TargetPort = target.wiring.wireLinkPorts.IndexOf(b),
            Wire = wire
        });
        return true;
    }

    // The hole filler (PR 4).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Customization), nameof(Customization.RemoveHole))]
    private static bool FillHole(Customization __instance, Collider holeCollider, ref bool __result)
    {
        if (!Intercepts || holeCollider == null || __instance is not TrainCarCustomization { TrainCar: { } car } ||
            !NetworkedTrainCar.TryGetNetId(car, out ushort carNetId) || !__instance.IsHole(holeCollider))
            return true;
        __result = false;
        uint operation = ++nextOperation;
        pending[operation] = new Pending();
        NetworkClient.Instance?.SendGadgetFillHole(new ServerboundGadgetFillHolePacket
        {
            OperationId = operation,
            CarNetId = carNetId,
            LocalPosition = holeCollider.transform.localPosition
        });
        return false;
    }

    private static bool Request(GadgetBase target, GadgetBase.GadgetRemovalMethod method, GadgetHandNonVR handNonVR, GadgetHandVR handVR)
    {
        if (!Intercepts || target == null || !target.IsLinked || !GadgetJoinApplier.IsServerSent(target))
            return false;
        NetworkedItem item = target.GadgetItem?.GetComponent<NetworkedItem>();
        if (item == null || item.NetId == 0)
            return false;
        if (IsPending(item))
            return true;
        uint operation = ++nextOperation;
        pending[operation] = new Pending { Item = item, Method = method, HandNonVR = handNonVR, HandVR = handVR };
        NetworkClient.Instance?.SendGadgetRemove(new ServerboundGadgetRemovePacket
        {
            OperationId = operation,
            ItemNetId = item.NetId,
            Method = (byte)method
        });
        return true;
    }

    private static bool IsPending(NetworkedItem item)
    {
        foreach (Pending request in pending.Values)
            if (request.Item == item)
                return true;
        return false;
    }

    /// <summary>
    /// The change log answered one of this player's requests (applied, so the gadget is on or off): a
    /// removal by hand ends with the item in that hand, as vanilla's does.
    /// </summary>
    internal static void Confirmed(GadgetChangeData entry)
    {
        if (entry.OperationId == 0 || entry.PlayerId != NetworkRole.LocalPlayerId ||
            !pending.TryGetValue(entry.OperationId, out Pending request))
            return;
        pending.Remove(entry.OperationId);
        if (entry.Kind != (byte)global::Multiplayer.Core.Gadgets.GadgetChangeKind.Removed || request.Item == null)
            return;
        if (request.HandNonVR != null && request.Item.TryGetComponent(out AGrabHandler grab))
            request.HandNonVR.grabber.InteractionHandler.RequestForceHold(grab);
        else if (request.HandVR != null && request.HandVR.lastGrab != null)
            request.HandVR.lastGrab.ForceGrabInteractable(request.Item.gameObject, usingGrabButton: true);
    }

    internal static void Rejected(ClientboundGadgetRejectPacket packet)
    {
        pending.Remove(packet.OperationId);
        Multiplayer.LogWarning($"The server refused a gadget request (item {packet.ItemNetId}): {packet.Reason}");
        if (!VRManager.IsVREnabled())
            SingletonBehaviour<InteractionTextControllerNonVr>.Instance?.DisplayText(packet.Reason);
    }
}
