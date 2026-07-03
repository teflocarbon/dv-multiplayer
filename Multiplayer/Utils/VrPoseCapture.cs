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
    // Name fragments used to locate the controller/hand transforms under the camera rig.
    // Adjust these if the logged hierarchy shows different names on your DV build.
    private static readonly string[] LeftHandNameHints = { "lefthand", "hand_left", "controller (left)", "leftcontroller", "hand left" };
    private static readonly string[] RightHandNameHints = { "righthand", "hand_right", "controller (right)", "rightcontroller", "hand right" };

    private static Transform leftHand;
    private static Transform rightHand;
    private static bool resolveAttempted;

    /// <summary>Explicit overrides. If set (e.g. from a future DV-version-specific patch), resolution is skipped.</summary>
    public static Transform LeftHandOverride { get; set; }
    public static Transform RightHandOverride { get; set; }

    /// <summary>Call when leaving a world so stale rig transforms aren't reused.</summary>
    public static void Reset()
    {
        leftHand = rightHand = null;
        resolveAttempted = false;
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

        if ((lh == null || rh == null) && !resolveAttempted)
        {
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
        resolveAttempted = true;

        // The hands live under the camera rig (the camera's parent chain). Search from the
        // highest rig ancestor we can find so both controllers are in scope.
        Transform rigRoot = head;
        for (int i = 0; i < 4 && rigRoot.parent != null; i++)
            rigRoot = rigRoot.parent;

        foreach (Transform t in rigRoot.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (leftHand == null && MatchesAny(n, LeftHandNameHints))
                leftHand = t;
            else if (rightHand == null && MatchesAny(n, RightHandNameHints))
                rightHand = t;
        }

        if (leftHand == null || rightHand == null)
        {
            Multiplayer.LogWarning(
                $"VrPoseCapture: could not resolve both hand transforms (left={leftHand?.name ?? "null"}, right={rightHand?.name ?? "null"}). " +
                $"VR IK arms will not be sent. Rig hierarchy under '{rigRoot.name}':");
            Multiplayer.LogWarning(DumpHierarchy(rigRoot, 0));
        }
        else
        {
            Multiplayer.Log($"VrPoseCapture: resolved hands left='{leftHand.name}', right='{rightHand.name}'.");
        }
    }

    private static bool MatchesAny(string name, string[] hints)
    {
        foreach (string h in hints)
            if (name.Contains(h))
                return true;
        return false;
    }

    private static string DumpHierarchy(Transform t, int depth)
    {
        string line = new string(' ', depth * 2) + t.name + "\r\n";
        foreach (Transform c in t)
            line += DumpHierarchy(c, depth + 1);
        return line;
    }
}
