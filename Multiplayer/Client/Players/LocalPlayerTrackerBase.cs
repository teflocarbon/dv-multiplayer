using Multiplayer.Shared.Protocol.Data.Player;
using Multiplayer.Shared.Protocol.Packets.Serverbound;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using System;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Client.Players;

public abstract class LocalPlayerTrackerBase : MonoBehaviour, ILocalPlayerTracker
{
    internal const float ROTATION_THRESHOLD = 0.2f;

    internal CustomFirstPersonController fps;

    internal bool lastOnCar;
    internal ushort lastCarNetId;
    internal Vector3 lastPosition;
    internal float lastRotationY;
    internal float lastLookPosition;
    internal float lastSitHeight;
    internal bool sentFinalPosition;

    // Updates go unreliable and carry only changed fields, so a lost packet could leave this
    // player's position, look or seat height stale for others until it changed again (and the
    // reliable "stopped" packet carried nothing, since nothing had changed that tick). The stop
    // packet and a periodic keyframe while active now carry the full state.
    private const float KeyframeIntervalSeconds = 1f;
    private float nextKeyframeAt;
    private float lastActivityAt = float.NegativeInfinity;
    internal PlayerPostureFlags lastPosture;

    internal bool isJumping;
    internal bool isOnCar;
    internal float sitHeight;
    internal TrainCar car;

    protected virtual void Start()
    {
        CoroutineManager.Instance.StartCoroutine(WaitForCustomFPS());
    }

    IEnumerator WaitForCustomFPS()
    {
        yield return null;

        while (fps == null && RequiresFirstPersonController)
        {
            fps = transform.GetComponent<CustomFirstPersonController>();
            yield return null;
        }

        Multiplayer.Log(fps == null
            ? $"{GetType().Name} initialising without CustomFirstPersonController."
            : "CustomFirstPersonController found. Continuing initialisation.");

        NetworkLifecycle.Instance.OnTick += OnTick;
        LocalPlayerTracker.Register(this);

        PlayerManager.CarChanged += OnCarChanged;
        CustomFirstPersonControllerPatch.OnJump += OnJump;

        Initialize();
    }

    protected abstract void Initialize();

    protected virtual bool RequiresFirstPersonController => true;

    protected virtual void OnDestroy()
    {
        LocalPlayerTracker.Unregister(this);
        if (UnloadWatcher.isQuitting)
            return;

        NetworkLifecycle.Instance.OnTick -= OnTick;

        PlayerManager.CarChanged -= OnCarChanged;
        CustomFirstPersonControllerPatch.OnJump -= OnJump;
    }

    protected void OnTick(uint tick)
    {
        // On the host the server samples this player itself, right before it handles the host's
        // requests (ILocalPlayerTracker), so its position is current for their range checks.
        if (NetworkRole.IsServer)
            return;

        ServerboundPlayerPositionPacket packet = Sample(out bool reliable);
        if (packet != null)
            NetworkLifecycle.Instance.Client.SendPlayerPosition(packet, reliable);
    }

    public ServerboundPlayerPositionPacket Sample(out bool reliable)
    {
        reliable = false;
        if (UnloadWatcher.isUnloading)
            return null;

        if (isOnCar && car == null)
        {
            car = PlayerManager.Car;
            isOnCar = car != null;
        }

        Vector3 position = isOnCar ? PlayerManager.PlayerTransform.localPosition : PlayerManager.PlayerTransform.GetWorldAbsolutePosition();
        float rotationY = PlayerManager.PlayerCamera.transform.eulerAngles.y;

        float rawHeadPitch = GetHeadPitch();
        float lookPosition = rawHeadPitch > 180f ? rawHeadPitch - 360f : rawHeadPitch;
        bool lookPositionChanged = Math.Abs(lastLookPosition - lookPosition) > ROTATION_THRESHOLD;

        PlayerPostureFlags posture = PlayerPostureFlags.None;

        bool underwater = fps != null && fps.underwater;
        if (!underwater)
        {
            if (fps != null && fps.IsCrouching)
                posture |= PlayerPostureFlags.Crouch;
            if (isJumping)
                posture |= PlayerPostureFlags.Jump;

            BuildPosture(ref posture);
        }
        else
        {
            posture = PlayerPostureFlags.Swim;
        }

        ushort carNetID = isOnCar ? car.GetNetId() : (ushort)0;

        bool positionOrRotationChanged = lastPosture != posture ||
                                        lastOnCar != isOnCar ||
                                        (isOnCar && lastCarNetId != carNetID) ||
                                        Vector3.Distance(lastPosition, position) > 0 ||
                                        Math.Abs(lastRotationY - rotationY) > ROTATION_THRESHOLD;

        bool heightChanged = lastSitHeight != sitHeight;

        bool moved = positionOrRotationChanged;
        bool stopping = !positionOrRotationChanged && !sentFinalPosition;
        if (moved || lookPositionChanged || heightChanged)
            lastActivityAt = Time.unscaledTime;
        // Keyframes while active and for a moment after (e.g. only looking around), not when idle.
        bool keyframe = stopping || (Time.unscaledTime >= nextKeyframeAt &&
                                     Time.unscaledTime - lastActivityAt <= KeyframeIntervalSeconds * 2f);
        if (keyframe)
        {
            positionOrRotationChanged = true;
            lookPositionChanged = true;
            heightChanged = true;
            nextKeyframeAt = Time.unscaledTime + KeyframeIntervalSeconds;
        }

        Vector3 moveDirV3 = fps != null
            ? PlayerManager.PlayerTransform.InverseTransformDirection(fps.m_MoveDir)
            : Vector3.zero;
        Vector2 moveDirection = new(moveDirV3.x, moveDirV3.z);

        var trackingData = new PlayerTrackingData
        {
            Position      = positionOrRotationChanged ? position                                                            : null,
            MoveDirection = positionOrRotationChanged ? moveDirection : null,
            RotationY     = positionOrRotationChanged ? rotationY                                                            : null,
            LookPosition  = lookPositionChanged       ? lookPosition                                                        : null,
            SitHeight     = heightChanged             ? sitHeight                                                           : null,
        };

        PopulateTrackingData(ref trackingData);

        bool hasChanges = positionOrRotationChanged || lookPositionChanged || heightChanged || trackingData.HasAdditionalData;

        if (!hasChanges && sentFinalPosition)
            return null;

        lastSitHeight = sitHeight;
        lastOnCar = isOnCar;
        lastCarNetId = carNetID;
        lastPosition = position;
        lastRotationY = rotationY;
        lastLookPosition = lookPosition;
        lastPosture = posture;
        sentFinalPosition = !moved;

        reliable = isJumping || sentFinalPosition;
        isJumping = false;
        return new ServerboundPlayerPositionPacket
        {
            TrackingData = trackingData,
            Posture = posture,
            IsOnCar = isOnCar,
            CarID = carNetID
        };
    }

    /// <summary>
    /// Returns the raw head pitch Euler angle.
    /// </summary>
    protected abstract float GetHeadPitch();

    /// <summary>
    /// Appends subclass-specific posture flags. Only called when not underwater.
    /// </summary>
    protected virtual void BuildPosture(ref PlayerPostureFlags posture) { }

    /// <summary>
    /// Populates subclass-specific fields in the tracking data struct.
    /// </summary>
    protected virtual void PopulateTrackingData(ref PlayerTrackingData data) { }

    void OnCarChanged(TrainCar trainCar)
    {
        isOnCar = trainCar != null;
        car = trainCar;
    }

    void OnJump()
    {
        isJumping = true;
    }
}
