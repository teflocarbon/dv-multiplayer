using Multiplayer.Shared.Protocol.Data.Player;
using UnityEngine;

namespace Multiplayer.Client.Players;

[DisallowMultipleComponent]
internal class LocalPlayerTrackerNonVR : LocalPlayerTrackerBase
{
    private LocomotionInputWrapper.LeanDirection lean;

    protected override void OnDestroy()
    {
        base.OnDestroy();

        // Leaving for the main menu can destroy the player controller before this.
        if (UnloadWatcher.isQuitting || fps == null)
            return;

        if (fps.Locomotion != null)
            fps.Locomotion.LeanDirectionChanged -= LeanDirectionChanged;

        if (fps.provider != null)
            fps.provider.OnPlayerHeightAdjusted -= OnPlayerHeightAdjusted;
    }

    protected override void Initialize()
    {
        if (fps.Locomotion != null)
            fps.Locomotion.LeanDirectionChanged += LeanDirectionChanged;

        fps.provider.OnPlayerHeightAdjusted += OnPlayerHeightAdjusted;
    }

    protected override float GetHeadPitch()
    {
        return fps.m_MouseLook.m_CameraTargetRot.eulerAngles.x;
    }

    protected override void BuildPosture(ref PlayerPostureFlags posture)
    {
        if (lean == LocomotionInputWrapper.LeanDirection.LeaningLeft)
            posture |= PlayerPostureFlags.LeanLeft;
        else if (lean == LocomotionInputWrapper.LeanDirection.LeaningRight)
            posture |= PlayerPostureFlags.LeanRight;

        if (fps.provider.IsSitting)
            posture |= PlayerPostureFlags.Sit;
    }

    private void LeanDirectionChanged(LocomotionInputWrapper.LeanDirection leanDirection)
    {
        lean = leanDirection;
    }

    private void OnPlayerHeightAdjusted(float newHeight, float _)
    {
        var clampedHeight= Mathf.Clamp(newHeight, CustomFirstPersonController.MIN_PLAYER_SITTING_HEIGHT, CustomFirstPersonController.MAX_PLAYER_SITTING_HEIGHT);
        sitHeight = Mathf.InverseLerp(CustomFirstPersonController.MIN_PLAYER_SITTING_HEIGHT, CustomFirstPersonController.MAX_PLAYER_SITTING_HEIGHT, clampedHeight);
    }
}
