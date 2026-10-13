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
    private const float MaxCommandAcceleration = 8f; // m/s², well above what DV trains do outside collisions

    private TrainCar trainCar;
    private NetworkedBogie[] bogies;
    private float targetSpeed;
    private float lastSampleTime = float.NegativeInfinity;
    private float commandedSpeed;
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

        float speed = targetSpeed;
        if (TryGetAlongTrackError(out float error) && Mathf.Abs(error) > CorrectionDeadband)
        {
            float limit = MaxCorrectionSpeed + Mathf.Abs(targetSpeed) * MaxCorrectionSpeedFraction;
            speed += Mathf.Clamp(error * CorrectionGain, -limit, limit);
        }

        // Samples arrive at the network tick rate, slower than physics, and carry some noise;
        // rate-limit the command so the car accelerates smoothly instead of in steps.
        if (!driving)
            commandedSpeed = Vector3.Dot(trainCar.rb.velocity, trainCar.transform.forward);
        driving = true;
        commandedSpeed = Mathf.MoveTowards(commandedSpeed, speed, MaxCommandAcceleration * Time.fixedDeltaTime);
        ApplySpeed(commandedSpeed);
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
