using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.Interaction;
using DV.UIFramework;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Client.Session;
using Multiplayer.Core.Gadgets;
using Multiplayer.Shared.Gadgets;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Protocol.Data.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Clientbound.Gadgets;
using Multiplayer.Shared.Protocol.Packets.Serverbound.Gadgets;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Client.Gadgets;

/// <summary>
/// A remote client's gadget actions become requests where vanilla would commit them (gadgets.md,
/// "Requests"), and are predicted (PR 3b, "Optimistic"): the change shows in this game at once, in the
/// apply scope, and the server's change log entry confirms it or its rejection undoes it, with every
/// later prediction, newest first (GadgetPredictionLog). Caught: <c>GadgetItem.TryPlace</c> (every install
/// path), the hammer's, the hand's and the telegrab's removals, <c>Drillable.SetMountPointState</c> (drill,
/// tape, untape), <c>Customization.RemoveHole</c> (hole filler) and <c>WireLinkPort.Wire</c>/<c>Unwire</c>
/// (crimping tool). Vanilla's own checks run first, so only what vanilla would have done is predicted.
/// An install takes a provisional negative UID until the server's arrives; a request naming one waits for
/// it. A removal is undone by resyncing the car (only the server still has the gadget's UID, soldering
/// and wires); one by hand ends, once confirmed, with the hand picking the item up as an ordinary grab.
/// Inside the apply scope, and in the host's game, they pass straight through. Labels (an install that
/// makes a new item) are gadget PR 9's.
/// </summary>
[HarmonyPatch]
internal static class GadgetIntentPatches
{
    private sealed class Pending
    {
        public NetworkedItem Item;
        // Who picks the item up once the removal is confirmed.
        public GadgetHandNonVR HandNonVR;
        public GadgetHandVR HandVR;
    }

    private static readonly Dictionary<uint, Pending> pending = new();
    // What undoes each prediction; a request held for a provisional UID gets the server's.
    private static readonly GadgetPredictionLog<Action, Action<int>> predictions = new();
    private static uint nextOperation;
    // What TryPlaceOnHit hit, which TryPlace isn't told.
    private static Mount hitMount;

    internal static void Reset()
    {
        pending.Clear();
        predictions.Clear();
    }

    private static bool Intercepts => NetworkRole.IsRemoteClient && !GadgetApplyScope.Active;

    private static uint NextOperation(Pending request = null)
    {
        uint operation = ++nextOperation;
        pending[operation] = request ?? new Pending();
        return operation;
    }

    private static void Predict(uint operation, Action apply, Action undo, int provisionalUid = 0)
    {
        using (GadgetApplyScope.Enter())
            apply();
        predictions.Predicted(operation, undo, provisionalUid);
    }

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
        if (!Intercepts)
            return true;
        if (instantiateGadget)
            return !Label(destination, position, rotation, gadgetItem, colliderForPlacementData, ref __result);
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
        Vector3 localPosition = frame.InverseTransformPoint(position);
        Quaternion localRotation = Quaternion.Inverse(frame.rotation) * rotation;
        Mount mount = null;
        int mountHostUid = 0;
        if (hitMount != null && gadgetItem.gadgetPrefab.RequiredMountPoints > 0 &&
            hitMount.GetComponent<GadgetBase>() is { IsLinked: true } host && host.Custom == destination)
        {
            mount = hitMount;
            mountHostUid = host.UID;
        }
        GadgetColliderPath.TryDescribe(car, colliderForPlacementData, out string colliderPath, out byte colliderIndex);

        uint operation = NextOperation(new Pending { Item = item });
        int provisionalUid = predictions.NewProvisionalUid();
        GadgetBase placed = null;
        gadgetItem.Item ??= item.GetComponent<ItemBase>();
        Predict(operation, () =>
        {
            gadgetItem.Gadget.AssignUID(provisionalUid);
            placed = GadgetItem.Place(destination, localPosition, localRotation, gadgetItem, colliderForPlacementData);
            if (placed != null && mount != null)
                mount.MountGadget(placed);
            if (placed != null)
                GadgetJoinApplier.ServerSent.Add(placed);
        }, () => UndoInstall(item), provisionalUid);
        if (placed != null)
            (placed.SoundOnPlaced != null ? placed.SoundOnPlaced : SingletonBehaviour<GadgetSystemUtility>.Instance.SoundOnGadgetPlaced)
                ?.Play(position);
        gadget = placed;

        void Send(int hostUid) => NetworkClient.Instance?.SendGadgetInstall(new ServerboundGadgetInstallPacket
        {
            OperationId = operation,
            ItemNetId = item.NetId,
            CarNetId = carNetId,
            LocalPosition = localPosition,
            LocalRotation = localRotation,
            MountHostUid = hostUid,
            ColliderPath = colliderPath,
            ColliderIndex = colliderIndex
        });
        if (GadgetPredictionLog<Action, Action<int>>.IsProvisional(mountHostUid))
            predictions.Hold(mountHostUid, Send);
        else
            Send(mountHostUid);
        return false;
    }

    /// <summary>
    /// The label maker (PR 9): a label is a new item, which only the server makes, so nothing is placed
    /// here. The text popup opens from the label maker's own label template, and its text goes with the
    /// pose in a <c>SpawnInstall</c>; the change log's <c>Installed</c> brings the label. A cancel sends
    /// nothing.
    /// </summary>
    private static bool Label(Customization destination, Vector3 position, Quaternion rotation, GadgetItem maker, Collider collider, ref bool result)
    {
        result = false;
        if (maker == null || maker.Gadget == null || maker.Gadget.GetComponentInChildren<TextGadget>(true) is not { } template ||
            !GadgetItem.CanPlace(position, rotation, maker.gadgetPrefab) ||
            destination is not TrainCarCustomization { TrainCar: { } car } || !NetworkedTrainCar.TryGetNetId(car, out ushort carNetId) ||
            maker.GetComponent<NetworkedItem>() is not { NetId: not 0 } tool)
            return true;
        Transform frame = destination.GetParentingTransform();
        Vector3 localPosition = frame.InverseTransformPoint(position);
        Quaternion localRotation = Quaternion.Inverse(frame.rotation) * rotation;
        int mountHostUid = hitMount != null && hitMount.GetComponent<GadgetBase>() is { IsLinked: true } host && host.Custom == destination &&
                           maker.gadgetPrefab.RequiredMountPoints > 0 ? host.UID : 0;
        GadgetColliderPath.TryDescribe(car, collider, out string colliderPath, out byte colliderIndex);
        template.ShowPopup(useExistingText: false, removeButton: false, resultHandler: (ok, text) =>
        {
            if (!ok || string.IsNullOrEmpty(text))
                return;
            NetworkClient.Instance?.SendGadgetSpawnInstall(new ServerboundGadgetSpawnInstallPacket
            {
                OperationId = NextOperation(),
                ToolItemNetId = tool.NetId,
                CarNetId = carNetId,
                LocalPosition = localPosition,
                LocalRotation = localRotation,
                MountHostUid = mountHostUid,
                ColliderPath = colliderPath,
                ColliderIndex = colliderIndex,
                Text = text
            });
        });
        return true;
    }

    // An installed label's popup (PR 9): a new text shows at once and goes to the server; deleting the
    // label asks the server, whose removal destroys its item and makes the replacement.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TextGadget), "PopupClosed")]
    private static bool LabelPopupClosed(TextGadget __instance, PopupResult result)
    {
        if (!Intercepts || __instance.ThisGadget is not { IsLinked: true } gadget || !GadgetJoinApplier.IsServerSent(gadget) ||
            gadget.GadgetItem?.GetComponent<NetworkedItem>() is not { NetId: not 0 } item)
            return true;
        if (result.closedBy == PopupClosedByAction.Positive)
        {
            __instance.textMesh.text = result.data;
            __instance.UpdateItemText();
            gadget.PlayPlaceSound(__instance.soundSourceOverride);
        }
        if (__instance.currentPopup != null)
            __instance.currentPopup.Closed -= __instance.PopupClosed;
        __instance.currentPopup = null;
        TextGadget.PopupResultHandler handler = __instance.currentResultHandler;
        __instance.currentResultHandler = null;
        handler?.Invoke(result.closedBy == PopupClosedByAction.Positive, result.data);
        if (result.closedBy is PopupClosedByAction.Positive or PopupClosedByAction.Negative)
            NetworkClient.Instance?.SendGadgetText(new ServerboundGadgetTextPacket
            {
                OperationId = NextOperation(),
                ItemNetId = item.NetId,
                Text = result.closedBy == PopupClosedByAction.Positive ? result.data ?? string.Empty : string.Empty,
                Remove = result.closedBy == PopupClosedByAction.Negative
            });
        return false;
    }

    // An item put on a gadget's hanger or shelf, or taken off it by a player (PR 10): vanilla snaps it here
    // at once (the prediction) and the server is told. A forced unsnap is the server's.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DV.Items.Snapping.ItemSnapPointBase), nameof(DV.Items.Snapping.ItemSnapPointBase.SnapItem),
        new[] { typeof(ItemBase), typeof(bool) })]
    private static void Snapping(DV.Items.Snapping.ItemSnapPointBase __instance, ItemBase itemToSnap, bool forced, out uint __state)
    {
        __state = 0;
        if (!Intercepts || forced || !SnapTarget(__instance, itemToSnap, out NetworkedItem gadgetItem, out NetworkedItem item) ||
            !__instance.CanSnapCheck(itemToSnap.SnappableItem, false))
            return;
        __state = SendSnap(gadgetItem, item, snap: true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DV.Items.Snapping.ItemSnapPointBase), nameof(DV.Items.Snapping.ItemSnapPointBase.SnapItem),
        new[] { typeof(ItemBase), typeof(bool) })]
    private static void Snapped(DV.Items.Snapping.ItemSnapPointBase __instance, ItemBase itemToSnap, bool __result, uint __state)
    {
        if (__state == 0)
            return;
        // Undone: off the hanger again, onto the floor.
        predictions.Predicted(__state, () =>
        {
            if (__instance != null && __instance.SnappedItem == itemToSnap && itemToSnap != null)
                using (GadgetApplyScope.Enter())
                    __instance.UnsnapItem(forced: true);
        });
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DV.Items.Snapping.ItemSnapPointBase), nameof(DV.Items.Snapping.ItemSnapPointBase.UnsnapItem))]
    private static void Unsnapping(DV.Items.Snapping.ItemSnapPointBase __instance, bool forced)
    {
        ItemBase snapped = __instance.SnappedItem;
        if (!Intercepts || forced || snapped == null || !SnapTarget(__instance, snapped, out NetworkedItem gadgetItem, out NetworkedItem item))
            return;
        uint operation = SendSnap(gadgetItem, item, snap: false);
        predictions.Predicted(operation, () =>
        {
            if (__instance != null && snapped != null && __instance.SnappedItem == null && !snapped.IsGrabbed())
                using (GadgetApplyScope.Enter())
                    __instance.SnapItem(snapped, forced: true);
        });
    }

    private static bool SnapTarget(DV.Items.Snapping.ItemSnapPointBase point, ItemBase item, out NetworkedItem gadgetItem, out NetworkedItem networked)
    {
        gadgetItem = null;
        networked = null;
        if (point is not SnapPointGadget { gadgetBase: { IsLinked: true } gadget } || !GadgetJoinApplier.IsServerSent(gadget) || item == null)
            return false;
        gadgetItem = gadget.GadgetItem?.GetComponent<NetworkedItem>();
        networked = item.GetComponent<NetworkedItem>();
        return gadgetItem is { NetId: not 0 } && networked is { NetId: not 0 };
    }

    private static uint SendSnap(NetworkedItem gadgetItem, NetworkedItem item, bool snap)
    {
        uint operation = NextOperation();
        NetworkClient.Instance?.SendGadgetSnap(new ServerboundGadgetSnapPacket
        {
            OperationId = operation,
            GadgetItemNetId = gadgetItem.NetId,
            ItemNetId = item.NetId,
            Snap = snap
        });
        return operation;
    }

    // A label coming off destroys its item and makes a replacement: items only the server makes.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DestroyOnGadgetUnlink), "OnUnlinked")]
    private static bool LabelItemSwap() => !NetworkRole.IsRemoteClient;

    // A refused install: off again (nothing of it is bolted, its own later predictions undone first), and
    // the item back in the hand.
    private static void UndoInstall(NetworkedItem item)
    {
        if (item == null || item.GetComponent<GadgetItem>()?.Gadget is not { IsLinked: true } gadget)
            return;
        using (GadgetApplyScope.Enter())
            gadget.Remove(reparentToTrainCar: false);
        GadgetJoinApplier.ServerSent.Remove(gadget);
        Resources.FindObjectsOfTypeAll<GadgetHandNonVR>().FirstOrDefault(h => h.gameObject.scene.IsValid())?.grabber?.InteractionHandler
            ?.RequestForceHold(item.GetComponent<AGrabHandler>());
    }

    // The hammer.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetRemover), "Remove")]
    private static bool HammerRemove(GadgetBase target, ref GadgetItem __result)
    {
        if (!Request(target, GadgetBase.GadgetRemovalMethod.Remover, null, null, forced: false))
            return true;
        __result = target.GadgetItem;
        return false;
    }

    // An empty hand, non-VR: vanilla removes it and makes the hand hold it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetHandNonVR), "TryGrab")]
    private static bool HandRemove(GadgetHandNonVR __instance, GadgetBase target, ref bool __result)
    {
        if (!Request(target, GadgetBase.GadgetRemovalMethod.EmptyHand, __instance, null, forced: false))
            return true;
        __result = true;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetHandVR), "TryGrab")]
    private static bool HandRemoveVR(GadgetHandVR __instance, GadgetBase target, ref bool __result)
    {
        if (!Request(target, GadgetBase.GadgetRemovalMethod.EmptyHand, null, __instance, forced: false))
            return true;
        __result = true;
        return false;
    }

    // A VR telegrab of an installed gadget pulls it off; the item lands where it was, to telegrab again.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GadgetBase), nameof(GadgetBase.OnTelegrabbableChanged))]
    private static bool TelegrabRemove(GadgetBase __instance, bool isBeingTelegrabbed) =>
        !(isBeingTelegrabbed && __instance.IsLinked &&
          Request(__instance, GadgetBase.GadgetRemovalMethod.EmptyHand, null, null, forced: true));

    private static bool Request(GadgetBase target, GadgetBase.GadgetRemovalMethod method, GadgetHandNonVR handNonVR,
        GadgetHandVR handVR, bool forced)
    {
        if (!Intercepts || target == null || !target.IsLinked || !GadgetJoinApplier.IsServerSent(target))
            return false;
        NetworkedItem item = target.GadgetItem?.GetComponent<NetworkedItem>();
        if (item == null || item.NetId == 0 || target.Custom is not TrainCarCustomization { TrainCar: { } car } ||
            !NetworkedTrainCar.TryGetNetId(car, out ushort carNetId))
            return false;
        if (IsPending(item))
            return true;
        uint operation = NextOperation(new Pending { Item = item, HandNonVR = handNonVR, HandVR = handVR });
        Predict(operation, () =>
        {
            if (forced)
                target.ForceRemove();
            else
                target.Remove();
        }, () => GadgetChangeApplier.Resync(carNetId, "a removal was refused"));
        NetworkClient.Instance?.SendGadgetRemove(new ServerboundGadgetRemovePacket
        {
            OperationId = operation,
            ItemNetId = item.NetId,
            Method = (byte)method
        });
        return true;
    }

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
        MountPoint.States before = __instance.GetMountPointState(index);
        uint operation = NextOperation();
        Predict(operation, () => __instance.SetMountPointState(index, newState), () =>
        {
            if (__instance != null && gadget != null && gadget.IsLinked)
                using (GadgetApplyScope.Enter())
                    __instance.SetMountPointState(index, before);
        });
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
        bool ready = wire ? GadgetWiringModule.WireLinkPort.ReadyToWire(a, b) : GadgetWiringModule.WireLinkPort.AreWired(a, b);
        if (!ready)
            return false;   // vanilla's own answer: nothing to do
        uint operation = NextOperation();
        Predict(operation, () => Apply(wire), () =>
        {
            using (GadgetApplyScope.Enter())
                Apply(!wire);
        });
        result = true;

        void Apply(bool on)
        {
            if (on)
                GadgetWiringModule.WireLinkPort.Wire(a, b);
            else
                GadgetWiringModule.WireLinkPort.Unwire(a, b);
        }

        int sourcePort = source.wiring.wireLinkPorts.IndexOf(a), targetPort = target.wiring.wireLinkPorts.IndexOf(b);
        SendWire(operation, carNetId, source.UID, sourcePort, target.UID, targetPort, wire);
        return true;
    }

    // A wire to a gadget whose install isn't confirmed yet waits for its UID.
    private static void SendWire(uint operation, ushort carNetId, int sourceUid, int sourcePort, int targetUid, int targetPort, bool wire)
    {
        if (GadgetPredictionLog<Action, Action<int>>.IsProvisional(sourceUid))
        {
            int provisional = sourceUid;
            predictions.Hold(provisional, uid => SendWire(operation, carNetId, uid, sourcePort, targetUid == provisional ? uid : targetUid, targetPort, wire));
            return;
        }
        if (GadgetPredictionLog<Action, Action<int>>.IsProvisional(targetUid))
        {
            int provisional = targetUid;
            predictions.Hold(provisional, uid => SendWire(operation, carNetId, sourceUid, sourcePort, uid, targetPort, wire));
            return;
        }
        NetworkClient.Instance?.SendGadgetWire(new ServerboundGadgetWirePacket
        {
            OperationId = operation,
            CarNetId = carNetId,
            SourceUid = sourceUid,
            SourcePort = sourcePort,
            TargetUid = targetUid,
            TargetPort = targetPort,
            Wire = wire
        });
    }

    // The hole filler (PR 4).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Customization), nameof(Customization.RemoveHole))]
    private static bool FillHole(Customization __instance, Collider holeCollider, ref bool __result)
    {
        if (!Intercepts || holeCollider == null || __instance is not TrainCarCustomization { TrainCar: { } car } ||
            !NetworkedTrainCar.TryGetNetId(car, out ushort carNetId) || !__instance.IsHole(holeCollider))
            return true;
        Vector3 position = holeCollider.transform.localPosition;
        Vector3 direction = holeCollider.transform.localRotation * Vector3.forward;
        uint operation = NextOperation();
        Predict(operation, () => __instance.RemoveHole(holeCollider), () =>
        {
            if (__instance != null)
                using (GadgetApplyScope.Enter())
                    __instance.AddHole(position, direction);
        });
        __result = true;
        NetworkClient.Instance?.SendGadgetFillHole(new ServerboundGadgetFillHolePacket
        {
            OperationId = operation,
            CarNetId = carNetId,
            LocalPosition = position
        });
        return false;
    }

    private static bool IsPending(NetworkedItem item)
    {
        foreach (Pending request in pending.Values)
            if (request.Item == item)
                return true;
        return false;
    }

    /// <summary>
    /// An entry from the server that answers one of this player's predictions, which already shows it: an
    /// install takes the server's UID (and the requests waiting for it go). Returns false for anything
    /// else, which the change applier then applies (an entry whose prediction a rejection undid too).
    /// </summary>
    internal static bool Reconcile(GadgetChangeData entry)
    {
        if (entry.OperationId == 0 || entry.PlayerId != NetworkRole.LocalPlayerId || !predictions.IsLive(entry.OperationId))
            return false;
        if (entry.Kind == (byte)GadgetChangeKind.Installed &&
            (!NetworkedItem.TryGet(entry.ItemNetId, out NetworkedItem item) || item.GetComponent<GadgetItem>()?.Gadget is not { IsLinked: true }))
            return false;   // the prediction didn't hold here: place it as the server did
        predictions.Confirm(entry.OperationId, out int provisionalUid);
        if (entry.Kind == (byte)GadgetChangeKind.Installed &&
            NetworkedItem.TryGet(entry.ItemNetId, out NetworkedItem installed) && installed.GetComponent<GadgetItem>()?.Gadget is { } gadget)
        {
            gadget.AssignUID(entry.Uid);
            GadgetStateClient.Track(gadget);
        }
        if (provisionalUid != 0)
            foreach (Action<int> held in predictions.Release(provisionalUid))
                held(entry.Uid);
        Confirmed(entry);
        return true;
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
        if (entry.Kind != (byte)GadgetChangeKind.Removed || request.Item == null)
            return;
        if (request.HandNonVR != null && request.Item.TryGetComponent(out AGrabHandler grab))
            request.HandNonVR.grabber.InteractionHandler.RequestForceHold(grab);
        else if (request.HandVR != null && request.HandVR.lastGrab != null)
            request.HandVR.lastGrab.ForceGrabInteractable(request.Item.gameObject, usingGrabButton: true);
    }

    internal static void Rejected(ClientboundGadgetRejectPacket packet)
    {
        pending.Remove(packet.OperationId);
        foreach (Action undo in predictions.Reject(packet.OperationId))
        {
            try
            {
                undo();
            }
            catch (Exception exception)
            {
                Multiplayer.LogException("Undoing a refused gadget prediction failed", exception);
            }
        }
        Multiplayer.LogWarning($"The server refused a gadget request (item {packet.ItemNetId}): {packet.Reason}");
        if (!VRManager.IsVREnabled())
            SingletonBehaviour<InteractionTextControllerNonVr>.Instance?.DisplayText(packet.Reason);
    }
}
