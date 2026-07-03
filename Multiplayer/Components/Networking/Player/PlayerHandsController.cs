using System.Linq;
using UnityEngine;

namespace Multiplayer.Components.Networking.Player;

/// <summary>
/// Renders a remote VR player as a floating head + hands, reusing Derail Valley's own detailed
/// hand models (cloned via <see cref="LocalVrHands"/>) driven by networked poses. Added at runtime
/// by <see cref="NetworkedPlayer"/> the first time a VR pose arrives.
/// </summary>
/// <remarks>
/// Poses arrive in the avatar-root's local space and the mount transforms are direct children of
/// that root, so we assign <see cref="Transform.localPosition"/>/<see cref="Transform.localRotation"/>
/// directly. While active, the walk-animation body is hidden. If the local player isn't in VR there
/// are no hands to clone, so <see cref="Initialise"/> fails and the avatar keeps the walk animation.
/// </remarks>
public class PlayerHandsController : MonoBehaviour
{
    private const float LERP_SPEED = 14.0f;
    private const float HEAD_SIZE = 0.24f;

    private Transform headMount;
    private Transform leftMount;
    private Transform rightMount;
    private SkinnedMeshRenderer[] bodyRenderers;

    private Vector3 headPos, leftPos, rightPos;
    private Quaternion headRot, leftRot, rightRot;
    private bool hasPose;

    /// <summary>Clones the local VR hands and builds the head/hand mounts. Returns false if unavailable.</summary>
    public bool Initialise()
    {
        if (!LocalVrHands.Available)
            return false; // viewer not in VR — nothing to clone

        GameObject lh = LocalVrHands.CloneHand(true);
        GameObject rh = LocalVrHands.CloneHand(false);
        if (lh == null || rh == null)
        {
            if (lh != null) Destroy(lh);
            if (rh != null) Destroy(rh);
            return false;
        }

        leftMount = new GameObject("VrHand_L").transform;
        rightMount = new GameObject("VrHand_R").transform;
        headMount = new GameObject("VrHead").transform;
        leftMount.SetParent(transform, false);
        rightMount.SetParent(transform, false);
        headMount.SetParent(transform, false);

        Attach(lh, leftMount);
        Attach(rh, rightMount);
        CreateHead(headMount);

        // Everything already on the avatar (the walk-anim character) is hidden while the VR
        // head+hands are shown. Exclude the hand clones we just parented under the mounts.
        bodyRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => !r.transform.IsChildOf(leftMount) && !r.transform.IsChildOf(rightMount))
            .ToArray();

        SetActive(false); // stay as body until the first pose lands
        return true;
    }

    /// <summary>Stores the latest pose (all in avatar-root local space) and activates on first receipt.</summary>
    public void SetPose(
        Vector3 head, Quaternion headRotation,
        Vector3 leftHand, Quaternion leftHandRotation,
        Vector3 rightHand, Quaternion rightHandRotation)
    {
        headPos = head;
        headRot = headRotation;
        leftPos = leftHand;
        leftRot = leftHandRotation;
        rightPos = rightHand;
        rightRot = rightHandRotation;

        if (!hasPose)
        {
            hasPose = true;
            SnapToPose();
            SetActive(true);
        }
    }

    private void Update()
    {
        if (!hasPose)
            return;

        float t = Time.deltaTime * LERP_SPEED;
        headMount.localPosition = Vector3.Lerp(headMount.localPosition, headPos, t);
        headMount.localRotation = Quaternion.Slerp(headMount.localRotation, headRot, t);
        leftMount.localPosition = Vector3.Lerp(leftMount.localPosition, leftPos, t);
        leftMount.localRotation = Quaternion.Slerp(leftMount.localRotation, leftRot, t);
        rightMount.localPosition = Vector3.Lerp(rightMount.localPosition, rightPos, t);
        rightMount.localRotation = Quaternion.Slerp(rightMount.localRotation, rightRot, t);
    }

    private void SnapToPose()
    {
        headMount.localPosition = headPos;
        headMount.localRotation = headRot;
        leftMount.localPosition = leftPos;
        leftMount.localRotation = leftRot;
        rightMount.localPosition = rightPos;
        rightMount.localRotation = rightRot;
    }

    private void SetActive(bool active)
    {
        if (bodyRenderers != null)
            foreach (SkinnedMeshRenderer r in bodyRenderers)
                if (r != null) r.enabled = !active;

        if (leftMount != null) leftMount.gameObject.SetActive(active);
        if (rightMount != null) rightMount.gameObject.SetActive(active);
        if (headMount != null) headMount.gameObject.SetActive(active);
    }

    private static void Attach(GameObject visual, Transform mount)
    {
        visual.transform.SetParent(mount, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
    }

    // Placeholder head so the avatar reads as a person, not disembodied hands. Trivial to swap
    // for a nicer head mesh later.
    private static void CreateHead(Transform mount)
    {
        GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "HeadPlaceholder";
        Collider col = head.GetComponent<Collider>();
        if (col != null) Destroy(col);
        head.transform.SetParent(mount, false);
        head.transform.localScale = Vector3.one * HEAD_SIZE;
    }

    private void OnDestroy()
    {
        // Restore the body if we're torn down (e.g. avatar removed).
        if (bodyRenderers != null)
            foreach (SkinnedMeshRenderer r in bodyRenderers)
                if (r != null) r.enabled = true;
    }
}
