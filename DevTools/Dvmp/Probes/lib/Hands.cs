// Shared probe library: compiled into every probe, so any probe can call Hands.* and LocalPlayer.*.
// Drives desktop (non-VR) item interaction through DV's real grabber pipeline, ported from the
// old runtime-test driver. Coroutine helpers are used with `yield return Hands.Pickup(id);`.
using System;
using System.Collections;
using System.Linq;
using DV.Interaction;
using DV.Interaction.Inputs;
using DV.UI;
using Dvmp.Agent;
using Multiplayer.Components.Networking.World;
using UnityEngine;

namespace Dvmp.Lib
{
    public static class Hands
    {
        // DV keys input/cursor requests by object identity, so keep one requester per process.
        private static object Requester
        {
            get
            {
                if (!DevContext.State.TryGetValue("dvmp.hands.requester", out object r) || r == null)
                    DevContext.State["dvmp.hands.requester"] = r = new object();
                return r;
            }
        }

        public static Grabber Grabber => UnityEngine.Object.FindObjectsOfType<Grabber>()
            .FirstOrDefault(g => g.GetComponent<GrabberInteractionHandlerDV>() != null && g.GetComponent<GrabberRaycasterDV>() != null);

        /// <summary>The networked item currently held by the local player, if any.</summary>
        public static NetworkedItem Held =>
            Grabber?.CurrentItemHeld != null ? Grabber.CurrentItemHeld.GetComponentInParent<NetworkedItem>() : null;

        /// <summary>
        /// Takes over input for automation: real keyboard/mouse are ignored (so stray input to a
        /// background window can't interfere) and the cursor is registered as locked so DV's
        /// raycaster works. Called automatically by the interaction helpers.
        /// </summary>
        public static void TakeControl()
        {
            if (VRManager.IsVREnabled()) throw new InvalidOperationException("Hands only drives desktop mode");
            InputManager.SetKeyboardAndMouseEnabled(Requester, false);
            CursorManager.Instance?.RequestCursor(Requester, true, 10000);
            ScreenspaceMouse.Instance?.RequestOverride(Requester, false, 10000);
        }

        public static void ReleaseControl()
        {
            InputManager.SetKeyboardAndMouseEnabled(Requester, true);
            CursorManager.Instance?.RemoveRequest(Requester);
            ScreenspaceMouse.Instance?.RemoveRequest(Requester);
        }

        /// <summary>
        /// Picks up an item by NetId: aims the camera at it and uses the normal raycast pickup,
        /// falling back to DV's force-hold when geometry blocks the raycast. Sets
        /// DevContext.State["dvmp.hands.path"] to "raycast" or "force-hold".
        /// </summary>
        public static IEnumerator Pickup(ushort netId, bool allowForceHold = true)
        {
            TakeControl();
            Grabber grabber = Grabber ?? throw new InvalidOperationException("local grabber not found");
            GrabberRaycasterDV raycaster = grabber.GetComponent<GrabberRaycasterDV>();
            GrabberInteractionHandlerDV interaction = grabber.GetComponent<GrabberInteractionHandlerDV>();
            if (grabber.CurrentItemHeld != null) throw new InvalidOperationException("already holding an item");
            if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null)
                throw new InvalidOperationException("item not found: " + netId);
            GrabHandlerItem requested = item.GetComponent<GrabHandlerItem>()
                                        ?? throw new InvalidOperationException("item has no desktop grab handler: " + netId);
            yield return null;

            yield return Aim(item);
            raycaster.UpdateRaycast();
            if (raycaster.CurrentlyRaycasted == requested)
            {
                interaction.RequestStartInteraction();
                DevContext.State["dvmp.hands.path"] = "raycast";
            }
            else
            {
                if (!allowForceHold)
                    throw new InvalidOperationException("raycast did not hit item " + netId);
                interaction.RequestForceHold(requested);
                DevContext.State["dvmp.hands.path"] = "force-hold";
            }
            yield return null;
            if (grabber.CurrentItemHeld != requested)
                throw new InvalidOperationException("pickup did not take item " + netId);
        }

        public static IEnumerator Drop()
        {
            Grabber grabber = Grabber ?? throw new InvalidOperationException("local grabber not found");
            AGrabHandler held = grabber.CurrentItemHeld ?? throw new InvalidOperationException("not holding anything");
            grabber.GetComponent<GrabberInteractionHandlerDV>().RequestDrop();
            yield return null;
            if (grabber.CurrentItemHeld != null) throw new InvalidOperationException("drop did not release " + held.name);
        }

        /// <summary>Throws the held item along <paramref name="direction"/> (camera forward by default).</summary>
        public static IEnumerator Throw(Vector3? direction = null)
        {
            Grabber grabber = Grabber ?? throw new InvalidOperationException("local grabber not found");
            GrabHandlerItem held = grabber.CurrentItemHeld as GrabHandlerItem ?? throw new InvalidOperationException("not holding a throwable item");
            Vector3 dir = (direction ?? PlayerManager.ActiveCamera.transform.forward).normalized;
            grabber.GetComponent<GrabberInteractionHandlerDV>().RequestDrop();
            held.Throw(dir);
            yield return null;
            yield return new WaitForFixedUpdate();
            if (grabber.CurrentItemHeld != null) throw new InvalidOperationException("throw did not release " + held.name);
        }

        /// <summary>Turns the desktop camera to look at the item (body yaw + camera pitch, like a player would).</summary>
        public static IEnumerator Aim(NetworkedItem item)
        {
            var controller = UnityEngine.Object.FindObjectOfType<CustomFirstPersonController>();
            Camera camera = PlayerManager.ActiveCamera;
            if (controller == null || camera == null) yield break;
            Collider collider = item.GetComponentsInChildren<Collider>(true).FirstOrDefault(c => c != null && c.enabled && !c.isTrigger);
            Vector3 target = collider != null ? collider.bounds.center : item.transform.position;
            Vector3 direction = target - camera.transform.position;
            if (direction.sqrMagnitude < 0.0001f) yield break;
            Quaternion rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            for (int frame = 0; frame < 4; frame++)
            {
                controller.ForceLookRotationNoTilt(rotation);
                yield return null;
            }
        }
    }

    public static class LocalPlayer
    {
        /// <summary>Teleports the local player to an absolute position (origin shift handled).</summary>
        public static IEnumerator TeleportAbs(Vector3 absolute, float settleSeconds = 1f)
        {
            // A falling player's controller can undo a teleport on the next frame; check and retry.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                PlayerManager.TeleportPlayer(Dev.World(absolute), PlayerManager.PlayerTransform.rotation, null, false);
                for (int i = 0; i < 3; i++) yield return null;
                if (Vector3.Distance(Dev.Abs(PlayerManager.PlayerTransform.position), absolute) < 5f)
                    break;
            }
            yield return new WaitForSeconds(settleSeconds);
        }

        /// <summary>Teleports the local player onto the ground at an absolute x/z. Keeping the old
        /// height when moving far can put the player inside a hill, and they fall through it.</summary>
        public static IEnumerator TeleportToGround(float absX, float absZ, float settleSeconds = 1f)
        {
            int ground = LayerMask.GetMask("Terrain");
            float? GroundY() =>
                Physics.Raycast(Dev.World(new Vector3(absX, 3000f, absZ)), Vector3.down, out RaycastHit hit, 5000f,
                    ground, QueryTriggerInteraction.Ignore) ? Dev.Abs(hit.point).y : (float?)null;
            float? y = GroundY();
            if (y == null)
            {
                // The terrain there isn't streamed in yet (a guessed height put players under
                // it once it loaded): wait above the spot until it is.
                yield return TeleportAbs(new Vector3(absX, 1500f, absZ), 0f);
                float t0 = Time.realtimeSinceStartup;
                while ((y = GroundY()) == null && Time.realtimeSinceStartup - t0 < 30f)
                    yield return null;
                if (y == null)
                    throw new InvalidOperationException($"terrain at ({absX:F0}, {absZ:F0}) never loaded");
            }
            yield return TeleportAbs(new Vector3(absX, y.Value + 0.3f, absZ), settleSeconds);
        }

        /// <summary>Teleports next to a networked item so it is within reach, facing it.</summary>
        public static IEnumerator GoTo(NetworkedItem item, float distance = 1.2f)
        {
            Vector3 itemAbs = Dev.Abs(item.transform.position);
            Vector3 from = Dev.PlayerAbs ?? itemAbs;
            Vector3 flat = new Vector3(from.x - itemAbs.x, 0, from.z - itemAbs.z);
            Vector3 offset = flat.sqrMagnitude > 0.01f ? flat.normalized * distance : new Vector3(distance, 0, 0);
            yield return TeleportAbs(itemAbs + offset + Vector3.up * 0.1f, 0.5f);
        }
    }
}
