using UnityEngine;

namespace Multiplayer.Components.Networking.Train;

/// <summary>
/// Client motion for an on-rail car. Every physics step the car runs at the host's speed plus a
/// proportional correction towards where the host's bogies are along the track (projected to
/// now). This replaces snapping the body to a latency-old position, which yanked moving trains
/// backwards every couple of seconds, and keeps locally simulated forces (brakes, rolling
/// resistance) from accumulating drift.
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

    private TrainCar trainCar;
    private NetworkedBogie[] bogies;
    private float targetSpeed;
    private float lastSampleTime = float.NegativeInfinity;
    private float easedSpeed;
    private float easedCorrection;
    private bool driving;

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
    }

    protected override void Process(float snapshot, uint snapshotTick)
    {
        targetSpeed = snapshot;
        lastSampleTime = Time.time;
    }

    private void FixedUpdate()
    {
        // The host only sends while a set is moving (plus a periodic resync); once samples stop,
        // leave the car to local physics as before.
        if (trainCar == null || trainCar.derailed || trainCar.rb == null || trainCar.rb.isKinematic ||
            Time.time - lastSampleTime > StaleSampleSeconds || NetworkLifecycle.Instance.IsHost())
        {
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
        // steps. Taking over a moving car starts from its own speed so it doesn't jerk.
        if (!driving)
        {
            easedSpeed = targetSpeed;
            easedCorrection = Vector3.Dot(trainCar.rb.velocity, trainCar.transform.forward) - targetSpeed;
        }
        driving = true;
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
