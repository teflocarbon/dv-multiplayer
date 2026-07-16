#if DEBUG
using DV.Interaction;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Debugging.RuntimeTests;

internal sealed class RuntimeTestUnsupportedException : Exception
{
    public RuntimeTestUnsupportedException(string message) : base(message) { }
}

internal sealed class DerailValleyItemTestDriver
{
    private Grabber grabber;
    private GrabberRaycasterDV raycaster;
    private GrabberInteractionHandlerDV interaction;
    private IPlayerRig playerRig;

    public bool TryResolve(out string reason)
    {
        reason = string.Empty;
        if (VRManager.IsVREnabled()) { reason = "vr-item-driver-not-implemented"; return false; }
        if (grabber != null && raycaster != null && interaction != null && playerRig != null) return true;
        Grabber candidate = UnityEngine.Object.FindObjectsOfType<Grabber>()
            .FirstOrDefault(item => item != null && item.GetComponent<GrabberInteractionHandlerDV>() != null && item.GetComponent<GrabberRaycasterDV>() != null);
        if (candidate == null) { reason = "local-grabber-unavailable"; return false; }
        GrabberRaycasterDV candidateRaycaster = candidate.GetComponent<GrabberRaycasterDV>();
        GrabberInteractionHandlerDV candidateInteraction = candidate.GetComponent<GrabberInteractionHandlerDV>();
        IPlayerRig candidateRig = candidate.GetComponent<IPlayerRig>();
        if (candidateRaycaster == null || candidateInteraction == null || candidateRig == null)
        {
            reason = "incomplete-local-grabber-stack";
            return false;
        }
        grabber = candidate;
        raycaster = candidateRaycaster;
        interaction = candidateInteraction;
        playerRig = candidateRig;
        return true;
    }

    public void Invalidate()
    {
        grabber = null;
        raycaster = null;
        interaction = null;
        playerRig = null;
    }

    public IEnumerator Pickup(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireResolved();
        ushort netId = RequiredNetId(command);
        if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
            throw new InvalidOperationException("item-not-found:" + netId);
        GrabHandlerItem requested = item.GetComponent<GrabHandlerItem>();
        if (requested == null) throw new RuntimeTestUnsupportedException("item-has-no-non-vr-grab-handler:" + netId);
        if (grabber.CurrentItemHeld != null) throw new InvalidOperationException("grabber-already-holding-item");

        raycaster.UpdateRaycast();
        AGrabHandler selected = raycaster.CurrentlyRaycasted;
        ushort selectedNetId = selected == null ? (ushort)0 : selected.GetComponentInParent<NetworkedItem>()?.NetId ?? 0;
        lock (run)
        {
            run.Result["requestedNetId"] = netId;
            run.Result["raycastedNetId"] = selectedNetId;
            run.Result["raycastedHandler"] = selected?.GetType().FullName ?? string.Empty;
        }
        if (selected != requested)
            throw new InvalidOperationException($"raycast-target-mismatch:expected={netId},actual={selectedNetId}");

        interaction.RequestStartInteraction();
        yield return null;
        if (grabber.CurrentItemHeld != requested || !requested.IsGrabbed())
            throw new InvalidOperationException("pickup-did-not-enter-holding-state");
        lock (run)
        {
            run.Result["heldNetId"] = netId;
            run.Result["itemState"] = item.DebugCurrentState.ToString();
        }
    }

    public IEnumerator Drop(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireResolved();
        GrabHandlerItem held = RequireHeld(command, out NetworkedItem item);
        ushort netId = item.NetId;
        interaction.RequestDrop();
        yield return null;
        if (grabber.CurrentItemHeld != null || held.IsGrabbed())
            throw new InvalidOperationException("drop-did-not-release-held-item");
        lock (run)
        {
            run.Result["releasedNetId"] = netId;
            run.Result["itemState"] = item.DebugCurrentState.ToString();
            run.Result["positionAbsolute"] = DebugValueSnapshotter.Snapshot(item.transform.position - WorldMover.currentMove);
        }
    }

    public IEnumerator Throw(RuntimeTestCommandDto command, RuntimeTestRunDto run)
    {
        RequireResolved();
        GrabHandlerItem held = RequireHeld(command, out NetworkedItem item);
        ushort netId = item.NetId;
        Vector3 direction = Direction(command, playerRig.GetAttachPoint().forward);
        interaction.RequestDrop();
        held.Throw(direction);
        yield return null;
        yield return new WaitForFixedUpdate();
        if (grabber.CurrentItemHeld != null || held.IsGrabbed())
            throw new InvalidOperationException("throw-did-not-release-held-item");
        Rigidbody rigidbody = held.GetComponent<Rigidbody>();
        lock (run)
        {
            run.Result["thrownNetId"] = netId;
            run.Result["direction"] = DebugValueSnapshotter.Snapshot(direction);
            run.Result["velocity"] = DebugValueSnapshotter.Snapshot(rigidbody?.velocity);
            run.Result["itemState"] = item.DebugCurrentState.ToString();
        }
        if (rigidbody == null) throw new RuntimeTestUnsupportedException("held-item-has-no-rigidbody");
        if (rigidbody.velocity.sqrMagnitude < 0.0001f) throw new InvalidOperationException("throw-produced-no-velocity");
    }

    private void RequireResolved()
    {
        if (!TryResolve(out string reason)) throw new RuntimeTestUnsupportedException(reason);
    }

    private GrabHandlerItem RequireHeld(RuntimeTestCommandDto command, out NetworkedItem item)
    {
        GrabHandlerItem held = grabber.CurrentItemHeld as GrabHandlerItem;
        if (held == null) throw new InvalidOperationException("no-held-item");
        item = held.GetComponentInParent<NetworkedItem>();
        if (item == null || item.NetId == 0) throw new RuntimeTestUnsupportedException("held-item-is-not-networked");
        if (command.Parameters.TryGetValue("netId", out string value) && ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort expected) && expected != item.NetId)
            throw new InvalidOperationException($"held-item-mismatch:expected={expected},actual={item.NetId}");
        return held;
    }

    private static ushort RequiredNetId(RuntimeTestCommandDto command) =>
        command.Parameters.TryGetValue("netId", out string value) && ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort result) && result != 0
            ? result : throw new ArgumentException("missing-or-invalid-parameter:netId");

    private static Vector3 Direction(RuntimeTestCommandDto command, Vector3 fallback)
    {
        if (!TryFloat(command, "directionX", out float x) || !TryFloat(command, "directionY", out float y) || !TryFloat(command, "directionZ", out float z)) return fallback.normalized;
        Vector3 direction = new(x, y, z);
        if (direction.sqrMagnitude < 0.0001f) throw new ArgumentException("invalid-throw-direction");
        return direction.normalized;
    }

    private static bool TryFloat(RuntimeTestCommandDto command, string key, out float result)
    {
        result = 0f;
        return command.Parameters.TryGetValue(key, out string value) &&
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}
#endif
