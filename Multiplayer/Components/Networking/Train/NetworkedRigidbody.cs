using Multiplayer.Networking.Data.Train;
using System;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Components.Networking.Train;

public class NetworkedRigidbody : TickedQueue<RigidbodySnapshot>
{
    private const int MAX_FRAMES = 60;
    private const float MAX_EXTRAPOLATION_SECONDS = 0.15f;
    private const float POSITION_RESPONSE = 14f;
    private const float ROTATION_RESPONSE = 18f;
    private const float SNAP_DISTANCE = 20f;
    // How much faster than the host's own car a correction may move ours (m/s, degrees/s).
    // MovePosition on a dynamic body is a teleport: after a pile-up that played out a little
    // differently here, the host's pose overlaps our other cars, and moving straight there every
    // step made the physics shove everything apart (derailed cars at 9 m/s, knocking held cars
    // off their places for good). Slide into place instead.
    private const float MAX_CORRECTION_SPEED = 1.5f;
    private const float MAX_CORRECTION_DEGREES = 45f;
    private Rigidbody rigidbody;
    private RigidbodySnapshot target;
    private float targetReceivedAt;
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
            targetReceivedAt = Time.realtimeSinceStartup;
            hasTarget = true;

            RigidbodySnapshot.IncludedData flags =
                (RigidbodySnapshot.IncludedData)snapshot.IncludedDataFlags;
            // A kinematic (resting) car has no velocity to set, and writing a near-zero one to a
            // dynamic car only keeps it awake.
            bool setVelocity = !rigidbody.isKinematic &&
                (!flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity) || snapshot.Velocity.magnitude >= 0.05f);
            if (setVelocity && flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity))
                rigidbody.velocity = snapshot.Velocity;
            if (setVelocity && flags.HasFlag(RigidbodySnapshot.IncludedData.AngularVelocity))
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

        RigidbodySnapshot.IncludedData flags =
            (RigidbodySnapshot.IncludedData)target.IncludedDataFlags;
        float age = Mathf.Clamp(Time.realtimeSinceStartup - targetReceivedAt,
            0f, MAX_EXTRAPOLATION_SECONDS);

        if (flags.HasFlag(RigidbodySnapshot.IncludedData.Position))
        {
            Vector3 desired = target.Position + WorldMover.currentMove;
            if (flags.HasFlag(RigidbodySnapshot.IncludedData.Velocity))
                desired += target.Velocity * age;
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
