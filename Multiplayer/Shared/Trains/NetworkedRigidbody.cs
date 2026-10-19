using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Session;
using System;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Shared.Trains;

public class NetworkedRigidbody : TickedQueue<RigidbodySnapshot>
{
    private const int MAX_FRAMES = 60;
    // The same limit as the on-rail cars' bogie samples (NetworkedBogie): a derailed car coupled
    // to on-rail ones must be aimed at the same moment as them, or the couplers fight.
    private const float MAX_EXTRAPOLATION_SECONDS = 0.5f;
    // Our velocity is only replaced by the host's when it is this far off (m/s): a car that just
    // derailed, or was thrown differently here. Replacing it every sample jolted a derailed car
    // being dragged in a moving consist against its couplers 24 times a second.
    private const float VELOCITY_RESYNC = 1.5f;
    private const float ANGULAR_VELOCITY_RESYNC = 0.5f; // rad/s
    private const float POSITION_RESPONSE = 14f;
    private const float ROTATION_RESPONSE = 18f;
    // A dynamic car is steered by its velocity: the host's, plus this share per second of the way
    // to the host's pose (then clamped as below). Moving it there with MovePosition every step
    // was a teleport its coupler joints then answered: dragged between on-rail cars, which are
    // steered by velocity too (TrainSpeedQueue), it shook against its couplers every step
    // (derail-drag: the gap at its coupling changed 6x as fast as on the host).
    private const float POSITION_GAIN = 4f;
    private const float ROTATION_GAIN = 6f;
    private const float SNAP_DISTANCE = 20f;
    // How much faster than the host's own car a correction may move ours (m/s, degrees/s).
    // MovePosition on a dynamic body is a teleport: after a pile-up that played out a little
    // differently here, the host's pose overlaps our other cars, and moving straight there every
    // step made the physics shove everything apart (derailed cars at 9 m/s, knocking held cars
    // off their places for good). Slide into place instead.
    private const float MAX_CORRECTION_SPEED = 1.5f;
    private const float MAX_CORRECTION_DEGREES = 45f;
    private Rigidbody rigidbody;
    private TrainCar trainCar;
    private RigidbodySnapshot target;
    // When the host took the sample, in our Time.time, from the host tick: the time it was
    // received left out its trip and its wait for our tick, aiming the car ~0.3 m behind its
    // on-rail neighbours at 30 km/h.
    private float targetTime;
    private bool hasTarget;

    protected override void OnEnable()
    {
        StartCoroutine(WaitForRB());
    }

    protected IEnumerator WaitForRB()
    {
        int counter = 0;

        while (rigidbody == null && counter < MAX_FRAMES)
        {
            rigidbody = GetComponent<Rigidbody>();
            if (rigidbody == null)
            {
                counter++;
                yield return new WaitForEndOfFrame();
            }
        }

        base.OnEnable();

        if (rigidbody == null)
        {
            gameObject.TryGetComponent(out TrainCar car);

            Multiplayer.LogError($"{gameObject.name} ({car?.ID}): {nameof(NetworkedBogie)} requires a {nameof(Bogie)} component on the same GameObject! Waited {counter} iterations");
        }
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        hasTarget = false;
        target = null;
    }

    protected override void Process(RigidbodySnapshot snapshot, uint snapshotTick)
    {
        if (snapshot == null)
        {
            Multiplayer.LogError($"NetworkedRigidBody.Process() Snapshot NULL!");
            return;
        }

        try
        {
            // Keep the host velocities authoritative, but consume position and rotation
            // continuously in FixedUpdate. Applying every network sample as a MovePosition
            // here makes derailed cars visibly stop-and-jump between network ticks.
            target = snapshot;
            float ageSeconds = (NetworkLifecycle.Instance.Tick - (float)snapshotTick) / NetworkLifecycle.TICK_RATE;
            targetTime = Time.time - Mathf.Clamp(ageSeconds, 0f, MAX_EXTRAPOLATION_SECONDS);
            hasTarget = true;

            RigidbodySnapshot.IncludedData flags =
                (RigidbodySnapshot.IncludedData)snapshot.IncludedDataFlags;
            // A kinematic (resting) car has no velocity to set, and writing a near-zero one to a
            // dynamic car only keeps it awake.
            bool setVelocity = !rigidbody.isKinematic &&
                (!flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity) || snapshot.Velocity.magnitude >= 0.05f);
            // Without a pose to steer to, FixedUpdate leaves the velocity alone: set it here.
            bool steered = flags.HasFlag(RigidbodySnapshot.IncludedData.Position);
            if (setVelocity && !steered && flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity) &&
                (rigidbody.velocity - snapshot.Velocity).magnitude > VELOCITY_RESYNC)
                rigidbody.velocity = snapshot.Velocity;
            if (setVelocity && !flags.HasFlag(RigidbodySnapshot.IncludedData.Rotation) &&
                flags.HasFlag(RigidbodySnapshot.IncludedData.AngularVelocity) &&
                (rigidbody.angularVelocity - snapshot.AngularVelocity).magnitude > ANGULAR_VELOCITY_RESYNC)
                rigidbody.angularVelocity = snapshot.AngularVelocity;

            if (flags.HasFlag(RigidbodySnapshot.IncludedData.Position) &&
                Vector3.Distance(rigidbody.position,
                    snapshot.Position + WorldMover.currentMove) > SNAP_DISTANCE)
                snapshot.Apply(rigidbody);
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"NetworkedRigidBody.Process() {ex.Message}\r\n {ex.StackTrace}");
        }
    }

    private void FixedUpdate()
    {
        if (!hasTarget || rigidbody == null || target == null)
            return;

        // The host only sends these for a derailed car. Once ours is back on the rails the target
        // is the derailed pose it left: still sliding there after a rerail dragged the car off
        // the host's spot at MAX_CORRECTION_SPEED, against the on-rail follower, for good.
        if (trainCar == null)
            trainCar = GetComponent<TrainCar>();
        if (trainCar != null && !trainCar.derailed)
        {
            hasTarget = false;
            target = null;
            return;
        }

        RigidbodySnapshot.IncludedData flags =
            (RigidbodySnapshot.IncludedData)target.IncludedDataFlags;
        float age = Mathf.Clamp(Time.time - targetTime, 0f, MAX_EXTRAPOLATION_SECONDS);

        Vector3 hostVelocity = flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity) ? target.Velocity : Vector3.zero;
        Vector3 hostAngular = flags.HasFlag(RigidbodySnapshot.IncludedData.AngularVelocity) ? target.AngularVelocity : Vector3.zero;
        if (!rigidbody.isKinematic)
        {
            // At rest on the host and where the host has it: left alone, so it can sleep (and a
            // car in a pile-up isn't nudged into its neighbours every step).
            if (hostVelocity.sqrMagnitude < 0.0025f && hostAngular.sqrMagnitude < 0.0025f &&
                (!flags.HasFlag(RigidbodySnapshot.IncludedData.Position) ||
                 (target.Position + WorldMover.currentMove - rigidbody.position).sqrMagnitude < 0.0004f) &&
                (!flags.HasFlag(RigidbodySnapshot.IncludedData.Rotation) ||
                 Quaternion.Angle(target.Rotation, rigidbody.rotation) < 0.5f))
                return;
            if (flags.HasFlag(RigidbodySnapshot.IncludedData.Position))
            {
                Vector3 desired = target.Position + WorldMover.currentMove + hostVelocity * age;
                rigidbody.velocity = hostVelocity +
                    Vector3.ClampMagnitude((desired - rigidbody.position) * POSITION_GAIN, MAX_CORRECTION_SPEED);
            }
            if (flags.HasFlag(RigidbodySnapshot.IncludedData.Rotation))
            {
                Quaternion desired = target.Rotation;
                if (hostAngular.sqrMagnitude > 0.000001f)
                    desired = Quaternion.AngleAxis(hostAngular.magnitude * age * Mathf.Rad2Deg, hostAngular.normalized) * desired;
                (desired * Quaternion.Inverse(rigidbody.rotation)).ToAngleAxis(out float degrees, out Vector3 axis);
                if (degrees > 180f)
                    degrees -= 360f;
                Vector3 correction = float.IsInfinity(axis.x) || float.IsNaN(axis.x)
                    ? Vector3.zero
                    : axis * (degrees * Mathf.Deg2Rad * ROTATION_GAIN);
                rigidbody.angularVelocity = hostAngular +
                    Vector3.ClampMagnitude(correction, MAX_CORRECTION_DEGREES * Mathf.Deg2Rad);
            }
            return;
        }

        // A kinematic car isn't moved by its velocity: placed instead.
        if (flags.HasFlag(RigidbodySnapshot.IncludedData.Position))
        {
            Vector3 desired = target.Position + WorldMover.currentMove + hostVelocity * age;
            float blend = 1f - Mathf.Exp(-POSITION_RESPONSE * Time.fixedDeltaTime);
            float hostSpeed = flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity) ? target.Velocity.magnitude : 0f;
            float maxStep = (hostSpeed + MAX_CORRECTION_SPEED) * Time.fixedDeltaTime;
            rigidbody.MovePosition(Vector3.MoveTowards(rigidbody.position,
                Vector3.Lerp(rigidbody.position, desired, blend), maxStep));
        }

        if (flags.HasFlag(RigidbodySnapshot.IncludedData.Rotation))
        {
            Quaternion desired = target.Rotation;
            if (flags.HasFlag(RigidbodySnapshot.IncludedData.AngularVelocity) &&
                target.AngularVelocity.sqrMagnitude > 0.000001f)
            {
                float angle = target.AngularVelocity.magnitude * age * Mathf.Rad2Deg;
                desired = Quaternion.AngleAxis(angle, target.AngularVelocity.normalized) * desired;
            }
            float blend = 1f - Mathf.Exp(-ROTATION_RESPONSE * Time.fixedDeltaTime);
            float hostDegrees = flags.HasFlag(RigidbodySnapshot.IncludedData.AngularVelocity)
                ? target.AngularVelocity.magnitude * Mathf.Rad2Deg : 0f;
            float maxDegrees = (hostDegrees + MAX_CORRECTION_DEGREES) * Time.fixedDeltaTime;
            rigidbody.MoveRotation(Quaternion.RotateTowards(rigidbody.rotation,
                Quaternion.Slerp(rigidbody.rotation, desired, blend), maxDegrees));
        }
    }

    public override void Clear()
    {
        base.Clear();
        hasTarget = false;
        target = null;
    }
}
