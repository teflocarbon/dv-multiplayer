using UnityEngine;

namespace Multiplayer.Components.Networking.Train;

/// <summary>
/// Client motion for an on-rail car. Every physics step the car runs at the host's speed plus a
/// proportional correction towards where the host's bogies are along the track (projected to
/// now). This replaces snapping the body to a latency-old position, which yanked moving trains
/// backwards every couple of seconds, and keeps locally simulated forces (brakes, rolling
/// resistance) from accumulating drift. A car the host reports as stopped is held where the
/// host's stopped (see <see cref="Hold"/>).
/// </summary>
public class TrainSpeedQueue : TickedQueue<float>
{
    private const float CorrectionGain = 2f;            // 1/s: closes ~86% of an error in 1 s
    private const float CorrectionDeadband = 0.02f;     // metres
    private const float MaxCorrectionSpeed = 3f;        // m/s, plus a share of the host's speed
    private const float MaxCorrectionSpeedFraction = 0.25f;
    private const float StaleSampleSeconds = 0.5f;
    private const float MaxSpeedAcceleration = 8f;      // m/s², easing the host's speed between samples
    private const float MaxCorrectionAcceleration = 4f; // m/s², easing the position correction
    private const float ImpactSpeedStep = 1.5f;         // m/s jump in the host's speed: only impacts make one

    /// <summary>Host speeds below this (m/s) mean the host's car is stopped: hold it there.</summary>
    public const float HoldSpeed = 0.05f;
    private const float HoldDeadband = 0.05f;        // metres a held car may be off before it's corrected
    private const float HoldSettled = 0.02f;         // metres: a held car's correction stops here
    private const float HoldMinSpeed = 0.2f;         // m/s: slower than this, vanilla pins a braked bogie
    private const float HoldCorrectionDelay = 0.5f;  // seconds off before correcting (see Hold)
    private const float HoldMaxDriveSeconds = 5f;    // a held car that can't be moved is left alone ...
    private const float HoldRetrySeconds = 10f;      // ... for this long before trying again

    private TrainCar trainCar;
    private NetworkedBogie[] bogies;
    private float targetSpeed;
    private float lastSampleTime = float.NegativeInfinity;
    private float easedSpeed;
    private float easedCorrection;
    private bool driving;
    private float holdOffSince = float.NegativeInfinity;
    private float holdDriveSince;
    private float holdRetryAt = float.NegativeInfinity;

    protected override void OnEnable()
    {
        trainCar = GetComponent<TrainCar>();
        if (trainCar == null)
        {
            Multiplayer.LogError($"{gameObject.name}: {nameof(TrainSpeedQueue)} requires a {nameof(TrainCar)} component on the same GameObject!");
            return;
        }

        base.OnEnable();
    }

    public override void Clear()
    {
        base.Clear();
        lastSampleTime = float.NegativeInfinity;
        holdOffSince = float.NegativeInfinity;
        holdRetryAt = float.NegativeInfinity;
    }

    protected override void Process(float snapshot, uint snapshotTick)
    {
        targetSpeed = snapshot;
        lastSampleTime = Time.time;
    }

    private void FixedUpdate()
    {
        if (trainCar == null || trainCar.derailed || trainCar.rb == null || trainCar.rb.isKinematic ||
            float.IsNegativeInfinity(lastSampleTime) || NetworkLifecycle.Instance.IsHost())
        {
            driving = false;
            return;
        }

        if (Mathf.Abs(targetSpeed) < HoldSpeed)
        {
            Hold();
            return;
        }

        // The host sends every tick while a set moves. Samples stopping without the host saying
        // the car stopped means they were lost: leave the car to local physics at the host's last
        // speed, not still carrying a correction it no longer needs (it would roll on with it).
        if (Time.time - lastSampleTime > StaleSampleSeconds)
        {
            if (driving)
                ApplySpeed(easedSpeed);
            driving = false;
            return;
        }

        float correction = 0f;
        if (TryGetAlongTrackError(out float error) && Mathf.Abs(error) > CorrectionDeadband)
        {
            float limit = MaxCorrectionSpeed + Mathf.Abs(targetSpeed) * MaxCorrectionSpeedFraction;
            correction = Mathf.Clamp(error * CorrectionGain, -limit, limit);
        }

        // Samples arrive at the network tick rate, slower than physics, and carry some noise:
        // ease the host's speed and the correction so the car accelerates smoothly instead of in
        // steps. A car taken over from local physics starts at the host's speed: whatever it was
        // doing locally (e.g. launched by a local loco before the host's samples arrived) never
        // happened on the host, so carrying it into the correction only overshot further.
        if (!driving)
        {
            easedSpeed = targetSpeed;
            easedCorrection = 0f;
        }
        driving = true;
        // When the host's car stops, Hold carries on from here with a fresh budget.
        holdDriveSince = Time.time;
        holdRetryAt = float.NegativeInfinity;
        // A jump in the host's speed that easing isn't meant for is an impact (a shunt, a
        // collision) reversing the host's car at once. Easing all of it kept the car going for a
        // second, metres too far, then hauled it back hard (upstream #141). Take it at once but for
        // the last bit.
        float jump = targetSpeed - easedSpeed;
        if (Mathf.Abs(jump) > ImpactSpeedStep)
            easedSpeed += jump - Mathf.Sign(jump) * ImpactSpeedStep;
        easedSpeed = Mathf.MoveTowards(easedSpeed, targetSpeed, MaxSpeedAcceleration * Time.fixedDeltaTime);
        easedCorrection = Mathf.MoveTowards(easedCorrection, correction, MaxCorrectionAcceleration * Time.fixedDeltaTime);
        ApplySpeed(easedSpeed + easedCorrection);
    }

    /// <summary>
    /// The host's car is stopped. Bring ours to where it stopped and leave it there, so vanilla
    /// can put it to sleep like the host's. Writing a rigidbody's velocity wakes it, and an awake,
    /// unbraked car creeps and wakes every car on its track, so a car close enough is not touched
    /// at all. Corrections wait a moment: a local loco hitting a held car is pushing it before the
    /// host's samples for that car arrive, and correcting then would fight the push.
    /// </summary>
    private void Hold()
    {
        bool hasError = TryGetAlongTrackError(out float error);
        float off = hasError ? Mathf.Abs(error) : 0f;

        if (!driving)
        {
            if (off <= HoldDeadband || Time.time < holdRetryAt)
            {
                holdOffSince = float.NegativeInfinity;
                return;
            }
            if (float.IsNegativeInfinity(holdOffSince))
                holdOffSince = Time.time;
            if (Time.time - holdOffSince < HoldCorrectionDelay)
                return;

            if (trainCar.isEligibleForSleep)
                trainCar.ForceOptimizationState(false);
            easedSpeed = 0f;
            easedCorrection = 0f;
            holdDriveSince = Time.time;
            driving = true;
        }

        if (!hasError || off <= HoldSettled || Time.time - holdDriveSince > HoldMaxDriveSeconds)
        {
            // Where the host's car is (or it can't be moved, e.g. held by its brakes on a slope):
            // stop it and hand it back to vanilla.
            if (hasError && off > HoldSettled)
                holdRetryAt = Time.time + HoldRetrySeconds;
            ApplySpeed(0f);
            driving = false;
            holdOffSince = float.NegativeInfinity;
            return;
        }

        // Too slow and vanilla's slope pin zeroes a braked bogie's velocity every step.
        float correction = Mathf.Sign(error) * Mathf.Clamp(off * CorrectionGain, HoldMinSpeed, MaxCorrectionSpeed);
        easedSpeed = Mathf.MoveTowards(easedSpeed, 0f, MaxSpeedAcceleration * Time.fixedDeltaTime);
        easedCorrection = Mathf.MoveTowards(easedCorrection, correction, MaxCorrectionAcceleration * Time.fixedDeltaTime);
        ApplySpeed(easedSpeed + easedCorrection);
    }

    private bool TryGetAlongTrackError(out float error)
    {
        error = 0f;
        if (trainCar.Bogies == null || trainCar.Bogies.Length == 0)
            return false;
        if (bogies == null || bogies.Length != trainCar.Bogies.Length)
            bogies = new NetworkedBogie[trainCar.Bogies.Length];

        int samples = 0;
        for (int i = 0; i < bogies.Length; i++)
        {
            if (bogies[i] == null && trainCar.Bogies[i] != null)
                bogies[i] = trainCar.Bogies[i].GetComponent<NetworkedBogie>();
            if (bogies[i] != null && bogies[i].TryGetAlongTrackError(trainCar, out float bogieError))
            {
                error += bogieError;
                samples++;
            }
        }
        if (samples == 0)
            return false;
        error /= samples;
        return true;
    }

    private void ApplySpeed(float speed)
    {
        // TrainCar#SetForwardSpeed doesn't check for derailed bogies
        trainCar.rb.velocity = trainCar.transform.forward * speed;

        if (trainCar.Bogies != null)
        {
            foreach (Bogie bogey in trainCar.Bogies)
            {
                if (bogey != null && !bogey.HasDerailed)
                    bogey.rb.velocity = bogey.transform.forward * speed;
            }
        }
    }
}
