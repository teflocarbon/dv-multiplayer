using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Components.Networking.Player;

/// <summary>
/// Holds references to the local player's in-game VR hand models and clones them (visual only)
/// so remote players' avatars can be rendered with Derail Valley's own detailed hands.
/// </summary>
/// <remarks>
/// The source transforms are registered by <see cref="Multiplayer.Utils.VrPoseCapture"/> when it
/// resolves the local VR rig (the same transforms we read poses from). Because the hand models are
/// a loaded game asset on every client, each client clones its *own* local hands to represent
/// everyone else — nothing is shipped or sent over the wire. Only available when the local player
/// is in VR; desktop viewers fall back to the walk-animation avatar.
/// </remarks>
public static class LocalVrHands
{
    private static Transform leftSource;
    private static Transform rightSource;

    /// <summary>True when both local hand models are known and can be cloned.</summary>
    public static bool Available => leftSource != null && rightSource != null;

    /// <summary>Registers the local VR hand transforms (called by VrPoseCapture on resolve).</summary>
    public static void SetSources(Transform left, Transform right)
    {
        leftSource = left;
        rightSource = right;
    }

    public static void Reset()
    {
        leftSource = rightSource = null;
    }

    /// <summary>
    /// Returns a visual-only clone of a local hand model, or null if unavailable.
    /// The caller owns the returned GameObject (parent it and drive its transform).
    /// </summary>
    public static GameObject CloneHand(bool left)
    {
        Transform src = left ? leftSource : rightSource;
        if (src == null)
            return null;

        GameObject clone = Object.Instantiate(src.gameObject);
        clone.name = $"ClonedVrHand_{(left ? "L" : "R")}";
        clone.transform.localScale = src.lossyScale;
        StripToVisual(clone);

        int renderers = clone.GetComponentsInChildren<Renderer>(true).Length;
        if (renderers == 0)
            Multiplayer.LogWarning(
                $"LocalVrHands: cloned hand '{src.name}' has no renderers. The resolved hand transform is " +
                $"probably a controller anchor without the mesh — point VrPoseCapture at the transform that holds the hand model.");
        else
            Multiplayer.LogDebug(() => $"LocalVrHands: cloned {(left ? "left" : "right")} hand '{src.name}' with {renderers} renderer(s).");

        return clone;
    }

    // Keep only what draws the hand (renderers + their bone transforms); drop interaction,
    // physics and audio so the clone is inert. Animator is kept but disabled to freeze the
    // current finger pose rather than play the local player's animations.
    private static void StripToVisual(GameObject go)
    {
        foreach (Component comp in go.GetComponentsInChildren<Component>(true))
        {
            switch (comp)
            {
                case null:
                case Transform:
                case MeshFilter:
                case MeshRenderer:
                case SkinnedMeshRenderer:
                    continue;
                case Animator anim:
                    anim.enabled = false;
                    continue;
                default:
                    Object.Destroy(comp);
                    continue;
            }
        }
    }
}
