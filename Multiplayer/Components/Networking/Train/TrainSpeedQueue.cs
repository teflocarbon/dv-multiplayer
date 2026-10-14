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
    private const float SlowCorrectionSpeed = 0.5f;     // m/s, plus a larger share: see the limit in FixedUpdate
    private const float SlowCorrectionSpeedFraction = 0.5f;
    private const float StaleSampleSeconds = 0.5f;
    private const float MaxSpeedAcceleration = 8f;      // m/s², easing the host's speed between samples
    private const float MaxCorrectionAcceleration = 4f; // m/s², easing the position correction
    private const float ImpactSpeedStep = 1.5f;         // m/s jump in the host's speed: only impacts make one

    /// <summary>Host speeds below this (m/s) mean the host's car is stopped: hold it there. Vanilla's own
    /// stationary threshold: a car creeping faster is followed, or holding it stepped it along in
    /// slides a second apart.</summary>
    public const float HoldSpeed = 0.01f;
    // m/s a held car's host car must exceed before it is followed again. Crawling cars hovering
    // around HoldSpeed flipped between following and holding every sample (visibly move-stop).
    private const float HoldExitSpeed = 0.05f;
    private const float HoldDeadband = 0.05f;        // metres a held car may be off before it's placed
    private const float HoldSettled = 0.02f;         // metres: placement stops here
    private const float HoldCorrectionDelay = 0.5f;  // seconds off before placing (see Hold)
    private const float PlaceSpeed = 0.5f;           // m/s a held car slides into place ...
    private const float PlaceFastSpeed = 1f;         // ... or this, while more than PlaceFastDistance off
    private const float PlaceFastDistance = 1f;
    private const float ContactDistance = 1.2f;      // metres between couplers that count as touching
    private const float ContactCheckSeconds = 0.25f;

    private TrainCar trainCar;
    private NetworkedBogie[] bogies;
    private float targetSpeed;
    private float lastSampleTime = float.NegativeInfinity;
    private float easedSpeed;
    private float easedCorrection;
    private bool driving;
    private float holdOffSince = float.NegativeInfinity;
    private float nextContactCheck;
    private bool touchingFollowing;
    private bool touchingHeld;

    /// <summary>Following the host's samples of a moving car.</summary>
    public bool Following { get; private set; }

    /// <summary>Sliding into the host's stopped position; the car is kinematic meanwhile.</summary>
    public bool Placing { get; private set; }

    /// <summary>
    /// Placed and touching other stopped cars, so left kinematic: handing it back to physics let
    /// the buffer springs it rests against (compressed, as on the host) push it and its neighbour
    /// off their places, and the two then re-placed each other forever. Released as soon as
    /// anything touching it follows a moving host car, or its own host car moves.
    /// </summary>
    public bool Parked { get; private set; }

    /// <summary>Kinematic because of us (placing or parked): nothing else should change it.</summary>
    public bool Kinematic => Placing || Parked;

    /// <summary>The host's car is stopped and ours is held (or being placed) where it is.</summary>
    public bool Held { get; private set; }

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

    protected override void OnDisable()
    {
        Release();
        base.OnDisable();
    }

    public override void Clear()
    {
        base.Clear();
        Release();
        lastSampleTime = float.NegativeInfinity;
        holdOffSince = float.NegativeInfinity;
        Following = false;
        Held = false;
    }

    protected override void Process(float snapshot, uint snapshotTick)
    {
        targetSpeed = snapshot;
        lastSampleTime = Time.time;
    }

    private void FixedUpdate()
    {
        if (trainCar == null || trainCar.derailed || trainCar.rb == null || (trainCar.rb.isKinematic && !Kinematic) ||
            float.IsNegativeInfinity(lastSampleTime) || NetworkLifecycle.Instance.IsHost())
        {
            Release();
            driving = false;
            Following = false;
            Held = false;
            return;
        }

        Held = Mathf.Abs(targetSpeed) < (Held ? HoldExitSpeed : HoldSpeed);
        if (Held)
        {
            Following = false;
            Hold();
            return;
        }

        // The host's car is moving again.
        Release();
        Following = true;

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
        RefreshContacts();
        // Pushing a car the host has stopped (and ours has placed) back towards our host position
        // would shove it off its own: follow the host's speed only while touching one.
        if (!touchingHeld && TryGetAlongTrackError(out float error) && Mathf.Abs(error) > CorrectionDeadband)
        {
            // A fast train needs a fast correction to catch up after a stall, but a slow one
            // doesn't: after a pile-up, cars crawling buffer to buffer each a little off were
            // driven at 3 m/s into each other and never settled. Scale the limit with the speed.
            float speed = Mathf.Abs(targetSpeed);
            float limit = Mathf.Min(MaxCorrectionSpeed + speed * MaxCorrectionSpeedFraction,
                SlowCorrectionSpeed + speed * SlowCorrectionSpeedFraction);
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
    /// The host's car is stopped: put ours where it stopped and leave it there, so vanilla can put
    /// it to sleep like the host's. A car close enough is not touched at all (writing a velocity
    /// wakes it, and an awake car wakes every car on its track). One further off is slid along the
    /// track into place, kinematic, rather than driven there: velocity corrections of cars resting
    /// buffer to buffer (after a pile-up) shoved each other off their places and never settled,
    /// while placed cars can't be knocked and end at the host's poses, which don't overlap.
    /// Placement waits a moment, and for touching cars that still follow a moving host car: a
    /// local loco hitting a held car pushes it before the host's samples for it arrive.
    /// </summary>
    private void Hold()
    {
        bool hasError = TryGetAlongTrackError(out float error);
        float off = hasError ? Mathf.Abs(error) : 0f;

        if (driving)
        {
            // Was following the host's car, which has now stopped: stop ours with it.
            ApplySpeed(0f);
            driving = false;
        }

        if (Parked)
        {
            RefreshContacts();
            if (touchingFollowing || !touchingHeld)
                Release();
            else if (hasError && off > HoldDeadband)
            {
                // The host moved it a little (a nudge from a neighbour): place it again.
                Parked = false;
                Placing = true;
            }
            return;
        }

        if (Placing)
        {
            RefreshContacts();
            if (!hasError || touchingFollowing)
            {
                Release();
                return;
            }
            if (off <= HoldSettled)
            {
                if (touchingHeld)
                {
                    Placing = false;
                    Parked = true;
                }
                else
                    Release();
                return;
            }
            float speed = off > PlaceFastDistance ? PlaceFastSpeed : PlaceSpeed;
            PlaceAlong(Mathf.Sign(error) * Mathf.Min(off, speed * Time.fixedDeltaTime));
            return;
        }

        if (off <= HoldDeadband)
        {
            holdOffSince = float.NegativeInfinity;
            return;
        }
        if (float.IsNegativeInfinity(holdOffSince))
            holdOffSince = Time.time;
        if (Time.time - holdOffSince < HoldCorrectionDelay)
            return;
        RefreshContacts();
        if (touchingFollowing)
            return;
        BeginPlacing();
    }

    private void BeginPlacing()
    {
        if (Placing)
            return;
        if (trainCar.isEligibleForSleep)
            trainCar.ForceOptimizationState(false);
        SetKinematic(true);
        Placing = true;
    }

    /// <summary>Hands a placed or parked car back to physics, stopped.</summary>
    private void Release()
    {
        if (!Kinematic)
            return;
        Placing = false;
        Parked = false;
        holdOffSince = float.NegativeInfinity;
        if (trainCar == null || trainCar.rb == null)
            return;
        SetKinematic(false);
        ApplySpeed(0f);
    }

    private void SetKinematic(bool kinematic)
    {
        trainCar.rb.isKinematic = kinematic;
        if (trainCar.Bogies == null)
            return;
        foreach (Bogie bogie in trainCar.Bogies)
            if (bogie != null && !bogie.HasDerailed && bogie.rb != null)
                bogie.rb.isKinematic = kinematic;
    }

    /// <summary>Moves the (kinematic) car <paramref name="metres"/> along its forward axis on the track.</summary>
    private void PlaceAlong(float metres)
    {
        Vector3 shift = Vector3.zero;
        int moved = 0;
        for (int i = 0; i < bogies.Length; i++)
        {
            if (bogies[i] != null && bogies[i].TryPlaceAlong(trainCar, metres, out Vector3 bogieShift))
            {
                shift += bogieShift;
                moved++;
            }
        }
        if (moved == 0)
        {
            Release();
            return;
        }
        trainCar.rb.MovePosition(trainCar.rb.position + shift / moved);
    }

    /// <summary>Whether a car touching ours (couplers within <see cref="ContactDistance"/>,
    /// coupled or not) is following a moving host car, or held.</summary>
    private void RefreshContacts()
    {
        if (Time.time < nextContactCheck)
            return;
        nextContactCheck = Time.time + ContactCheckSeconds;
        touchingFollowing = touchingHeld = false;
        if (CarSpawner.Instance == null || trainCar.frontCoupler == null || trainCar.rearCoupler == null)
            return;
        Vector3 front = trainCar.frontCoupler.transform.position, rear = trainCar.rearCoupler.transform.position;
        float range = ContactDistance * ContactDistance;
        foreach (TrainCar other in CarSpawner.Instance.AllCars)
        {
            if (other == null || other == trainCar || other.frontCoupler == null || other.rearCoupler == null)
                continue;
            Vector3 a = other.frontCoupler.transform.position, b = other.rearCoupler.transform.position;
            if ((front - a).sqrMagnitude > range && (front - b).sqrMagnitude > range &&
                (rear - a).sqrMagnitude > range && (rear - b).sqrMagnitude > range)
                continue;
            if (!other.TryGetComponent(out TrainSpeedQueue queue))
                continue;
            touchingFollowing |= queue.Following;
            touchingHeld |= queue.Held;
        }
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
