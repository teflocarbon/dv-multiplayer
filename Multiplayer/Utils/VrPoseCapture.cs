using DV.Player;
using Multiplayer.Components.Networking.Player;
using UnityEngine;

namespace Multiplayer.Utils;

/// <summary>
/// Captures the local VR player's head and hand poses in the player body's local space,
/// ready to be sent to other clients and reproduced on their avatars (see PlayerHandsController).
/// </summary>
/// <remarks>
/// The head comes from <see cref="PlayerManager.PlayerCamera"/> (the HMD). The two hand
/// transforms are the only piece that depends on Derail Valley's VR rig internals, so they
/// are resolved once via <see cref="ResolveHands"/> and cached. If DV's rig names differ from
/// the filters below, the resolver logs the rig hierarchy so the filters can be adjusted.
/// </remarks>
public static class VrPoseCapture
{
    // Name fragments used to locate the hand transforms under the camera rig. These are tried in
    // PRIORITY order: the actual DV hand meshes first (e.g. "LeftHandDV"/"RightHandDV"), and the
    // controller anchors only as a last resort — the controller objects have no hand mesh, so
    // matching them produces an empty ("no renderers") clone. Adjust if the logged hierarchy differs.
    private static readonly string[] LeftHandNameHints = { "lefthanddv", "lefthand", "hand_left", "hand left" };
    private static readonly string[] RightHandNameHints = { "righthanddv", "righthand", "hand_right", "hand right" };
    // Fallbacks used only if no hand-mesh transform is found.
    private static readonly string[] LeftHandFallbackHints = { "controller (left)", "leftcontroller" };
    private static readonly string[] RightHandFallbackHints = { "controller (right)", "rightcontroller" };

    private static Transform leftHand;
    private static Transform rightHand;
    // Throttle for repeated resolve attempts (retried until both hands are found — see TryGetPose).
    private static float nextResolveTime;
    private static bool resolveFailureLogged;
    private const float RESOLVE_RETRY_INTERVAL = 1.0f;

    /// <summary>Explicit overrides. If set (e.g. from a future DV-version-specific patch), resolution is skipped.</summary>
    public static Transform LeftHandOverride { get; set; }
    public static Transform RightHandOverride { get; set; }

    /// <summary>Call when leaving a world so stale rig transforms aren't reused.</summary>
    public static void Reset()
    {
        leftHand = rightHand = null;
        nextResolveTime = 0f;
        resolveFailureLogged = false;
        LocalVrHands.Reset();
    }

    /// <summary>
    /// Attempts to read the current VR pose. Returns false if the rig transforms aren't available yet.
    /// All out values are in the local space of <paramref name="root"/> (the player body transform).
    /// </summary>
    public static bool TryGetPose(
        Transform root,
        out Vector3 headPos, out Quaternion headRot,
        out Vector3 leftHandPos, out Quaternion leftHandRot,
        out Vector3 rightHandPos, out Quaternion rightHandRot)
    {
        headPos = leftHandPos = rightHandPos = Vector3.zero;
        headRot = leftHandRot = rightHandRot = Quaternion.identity;

        Transform head = PlayerManager.PlayerCamera != null ? PlayerManager.PlayerCamera.transform : null;
        if (root == null || head == null)
            return false;

        Transform lh = LeftHandOverride != null ? LeftHandOverride : leftHand;
        Transform rh = RightHandOverride != null ? RightHandOverride : rightHand;

        // Retry resolution until both hands are found rather than latching on the first attempt.
        // The rig transforms may not exist/be named yet on the very first ticks after entering a
        // world; a single early failure must not permanently stop this player from sending poses.
        if ((lh == null || rh == null) && Time.unscaledTime >= nextResolveTime)
        {
            nextResolveTime = Time.unscaledTime + RESOLVE_RETRY_INTERVAL;
            ResolveHands(head);
            lh = LeftHandOverride != null ? LeftHandOverride : leftHand;
            rh = RightHandOverride != null ? RightHandOverride : rightHand;
        }

        if (lh == null || rh == null)
            return false;

        // Make the resolved hand models available for cloning onto remote players' avatars.
        if (!LocalVrHands.Available)
            LocalVrHands.SetSources(lh, rh);

        headPos = root.InverseTransformPoint(head.position);
        headRot = Quaternion.Inverse(root.rotation) * head.rotation;
        leftHandPos = root.InverseTransformPoint(lh.position);
        leftHandRot = Quaternion.Inverse(root.rotation) * lh.rotation;
        rightHandPos = root.InverseTransformPoint(rh.position);
        rightHandRot = Quaternion.Inverse(root.rotation) * rh.rotation;
        return true;
    }

    private static void ResolveHands(Transform head)
    {
        // The hands live under the camera rig (the camera's parent chain). Search from the
        // highest rig ancestor we can find so both controllers are in scope.
        Transform rigRoot = head;
        for (int i = 0; i < 4 && rigRoot.parent != null; i++)
            rigRoot = rigRoot.parent;

        Transform[] all = rigRoot.GetComponentsInChildren<Transform>(true);

        // Prefer the actual hand-mesh transforms; only fall back to the controller anchors if a
        // hand mesh can't be found (matching a controller anchor gives a mesh-less clone).
        leftHand = FindFirst(all, LeftHandNameHints) ?? FindFirst(all, LeftHandFallbackHints);
        rightHand = FindFirst(all, RightHandNameHints) ?? FindFirst(all, RightHandFallbackHints);

        if (leftHand == null || rightHand == null)
        {
            // Only dump the (large) hierarchy once — this method now retries every second until it
            // succeeds, so we mustn't spam the log on every attempt.
            if (!resolveFailureLogged)
            {
                resolveFailureLogged = true;
                Multiplayer.LogWarning(
                    $"VrPoseCapture: could not resolve both hand transforms yet (left={leftHand?.name ?? "null"}, right={rightHand?.name ?? "null"}). " +
                    $"Will keep retrying. Rig hierarchy under '{rigRoot.name}':");
                Multiplayer.LogWarning(DumpHierarchy(rigRoot, 0));
            }
        }
        else
        {
            Multiplayer.Log($"VrPoseCapture: resolved hands left='{leftHand.name}', right='{rightHand.name}'.");
        }
    }

    // Searches hint-by-hint so earlier (more specific) hints win even if a later-hint match appears
    // earlier in the hierarchy — e.g. "lefthanddv" is preferred over anything else containing "lefthand".
    private static Transform FindFirst(Transform[] transforms, string[] hints)
    {
        foreach (string h in hints)
            foreach (Transform t in transforms)
                if (t.name.ToLowerInvariant().Contains(h))
                    return t;
        return null;
    }

    private static string DumpHierarchy(Transform t, int depth)
    {
        string line = new string(' ', depth * 2) + t.name + "\r\n";
        foreach (Transform c in t)
            line += DumpHierarchy(c, depth + 1);
        return line;
    }
}
