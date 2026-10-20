using System.Collections.Generic;
using UnityEngine;
using Multiplayer.Core.Trains;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Trains;

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
    /// <summary>Sample intervals a car may go without one, beyond <see cref="StaleSampleSeconds"/>, before it's taken as lost (see <see cref="StaleSeconds"/>).</summary>
    public const float StaleSampleIntervals = 2f;
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
    private const float HoldCorrectionDelay = 0.5f;  // seconds off before correcting (see Hold)
    private const float PlaceSpeed = 0.5f;           // m/s a held car slides into place ...
    private const float PlaceFastSpeed = 1f;         // ... or this, while more than PlaceFastDistance off
    private const float PlaceFastDistance = 1f;
    private const float ContactDistance = 1.2f;      // metres between couplers that count as touching
    private const float ContactCheckSeconds = 0.25f;
    // Coupled cars are held by velocity (see HoldCoupled).
    private const float HoldMinSpeed = 0.2f;         // m/s: slower than this, vanilla pins a braked bogie
    private const float HoldMaxDriveSeconds = 10f;   // a held car that can't be moved is left alone ...
    private const float HoldRetrySeconds = 10f;      // ... for this long before trying again

    // This client's freezes (see MeasureFreeze, TryCatchUp).
    private static int measuredFrame = -1;
    private static float measuredRealTime, measuredGameTime;
    private static int freezes;
    private static float lastFreezeSeconds;
    private static float freezeEndedAt = float.NegativeInfinity;
    private static int freezeFrame = int.MinValue / 2;  // the frame the latest freeze was noticed in
    private static readonly List<TrainSpeedQueue> catchUpSet = [];

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
    private float nextContactCheck;
    private bool touchingFollowing;
    private bool touchingHeld;
    private int caughtUpAfter;                       // the freeze this car's set was caught up after
    private float correctFrom = float.NegativeInfinity; // fixed time: no correction before (just caught up)
    private Vector3[] catchUpShifts = [];
    private int receivedFrame = int.MinValue / 2;    // the frame the latest sample was received in
    private uint freshFromTick;                      // the first sample received since the latest freeze
    private uint processedTick;

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

    /// <summary>
    /// Whether the host has this car coupled to others (it sent it as part of a larger set). The
    /// client applies a coupling a moment after the host commits it; right after an impact that
    /// coupled the cars, ours could still be alone, and placing it alone as the coupler joint
    /// formed held the consist metres off.
    /// </summary>
    public bool HostCoupled { get; set; }

    /// <summary>
    /// Seconds between the host's samples of this car while it moves: a tick close by, up to a
    /// second for a far train (the host sends a set less often the further the player is from it).
    /// </summary>
    public float SampleIntervalSeconds { get; private set; } = 1f / NetworkLifecycle.TICK_RATE;
    private float receivedIntervalSeconds = 1f / NetworkLifecycle.TICK_RATE;

    /// <param name="intervalSeconds">When the host's next sample is due (the packet's interval).</param>
    public void ReceiveSnapshot(float speed, float intervalSeconds, uint tick)
    {
        // Taken up with the sample (Process), not now: a car coming back into close range would
        // otherwise judge the last far sample by the close interval for a tick, and go stale.
        receivedIntervalSeconds = intervalSeconds;
        // A frame's samples are received in its Update, after its physics steps noticed a freeze.
        if (receivedFrame < freezeFrame)
            freshFromTick = tick;
        receivedFrame = Time.frameCount;
        ReceiveSnapshot(speed, tick);
    }

    /// <summary>
    /// Waiting to be caught up after this client's freeze (<see cref="TryCatchUp"/>): a full sync
    /// doesn't snap it meanwhile. The full syncs a freeze leaves queued are seconds old (their age
    /// unknown: the client's tick fell behind too) and snapped the car to where the host's had
    /// been, before the catch-up put it where it is (client-freeze, q6: 11 m and 25 m snaps).
    /// </summary>
    public bool AwaitingCatchUp => caughtUpAfter != freezes && FreezeWindowOpen;

    // The second after a freeze, and its next two frames whatever their length: processing the
    // packets a 3 s freeze left took a 1 s frame of its own (q6).
    private static bool FreezeWindowOpen =>
        Time.frameCount - freezeFrame <= 2 || Time.realtimeSinceStartup - freezeEndedAt < FreezeCatchUp.WindowSeconds;

    /// <summary>
    /// How long (real time: samples arrive in real time) a moving car goes without a sample
    /// before they're taken as lost: 0.5 s close by, plus two sample intervals for a far train,
    /// so one or two lost samples are ridden out (1 s at 4 Hz, 2.5 s at 1 Hz). Unreliable samples
    /// do get lost: at 4 Hz a 0.9 s gap took a car as lost (train-rate, 2026-10-20).
    /// </summary>
    public static float StaleSeconds(float sampleIntervalSeconds) =>
        sampleIntervalSeconds * NetworkLifecycle.TICK_RATE <= 1.5f ? StaleSampleSeconds
            : StaleSampleSeconds + StaleSampleIntervals * sampleIntervalSeconds;

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
        holdRetryAt = float.NegativeInfinity;
        Following = false;
        Held = false;
    }

    protected override void Process(float snapshot, uint snapshotTick)
    {
        targetSpeed = snapshot;
        processedTick = snapshotTick;
        lastSampleTime = Time.realtimeSinceStartup;
        SampleIntervalSeconds = receivedIntervalSeconds;
    }

    private void FixedUpdate()
    {
        MeasureFreeze();
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

        // The host sends a moving set at a steady rate (every tick close by, less often far
        // away). Samples stopping without the host saying the car stopped means they were lost:
        // leave the car to local physics at the host's last speed, not still carrying a
        // correction it no longer needs (it would roll on with it).
        if (Time.realtimeSinceStartup - lastSampleTime > StaleSeconds(SampleIntervalSeconds))
        {
            if (driving)
                ApplySpeed(easedSpeed);
            driving = false;
            return;
        }

        if (AwaitingCatchUp && TryCatchUp())
            return;

        float correction = 0f;
        RefreshContacts();
        // Pushing a car the host has stopped (and ours has placed) back towards our host position
        // would shove it off its own: follow the host's speed only while touching one. Just after
        // a catch-up the bogies' travellers are still where the car was, until vanilla's next step.
        if (!touchingHeld && Time.fixedTime >= correctFrom &&
            TryGetAlongTrackError(out float error) && Mathf.Abs(error) > CorrectionDeadband)
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
        // When the host's car stops, HoldCoupled carries on from here with a fresh budget.
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

        // Placement slides one car on its own: a car coupled to others would fight the coupler
        // joint (and parking one end kinematic held the consist off its place after a coupling
        // impact). Coupled cars are held by velocity, which the joints carry along.
        if (HostCoupled || (trainCar.trainset != null && trainCar.trainset.cars.Count > 1))
        {
            Release();
            HoldCoupled(hasError, error, off);
            return;
        }

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

    /// <summary>
    /// Hold for a car coupled to others: drive it (and through the joints its consist) back to
    /// within 2 cm at 0.2-0.5 m/s, then stop it. Left alone within 5 cm so it can sleep.
    /// </summary>
    private void HoldCoupled(bool hasError, float error, float off)
    {
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

        float correction = Mathf.Sign(error) * Mathf.Clamp(off * CorrectionGain, HoldMinSpeed, SlowCorrectionSpeed);
        easedSpeed = Mathf.MoveTowards(easedSpeed, 0f, MaxSpeedAcceleration * Time.fixedDeltaTime);
        easedCorrection = Mathf.MoveTowards(easedCorrection, correction, MaxCorrectionAcceleration * Time.fixedDeltaTime);
        ApplySpeed(easedSpeed + easedCorrection);
    }

    /// <summary>
    /// Notes this client's freezes: a frame (or a stretch without physics, e.g. paused) in which
    /// real time ran on but game time hardly did, as DV caps a frame's delta. The host's trains
    /// ran on meanwhile.
    /// </summary>
    private static void MeasureFreeze()
    {
        if (Time.frameCount == measuredFrame)
            return;
        float real = Time.realtimeSinceStartup, game = Time.time;
        if (measuredFrame >= 0 && FreezeCatchUp.IsFreeze(real - measuredRealTime, game - measuredGameTime))
        {
            freezes++;
            lastFreezeSeconds = real - measuredRealTime - (game - measuredGameTime);
            freezeEndedAt = real;
            freezeFrame = Time.frameCount;
        }
        measuredFrame = Time.frameCount;
        measuredRealTime = real;
        measuredGameTime = game;
    }

    /// <summary>
    /// Following a sample received since the latest freeze. One received before it was queued by
    /// the frame that took the freeze's packets, which can itself be long (1 s after a 3 s
    /// freeze, q6): the host's train has run on since, further than a sample's projection goes
    /// (0.5 s), and catching up to it left the car 5 m behind.
    /// </summary>
    private bool HasFreshSample => Time.realtimeSinceStartup - lastSampleTime <= StaleSeconds(SampleIntervalSeconds) &&
                                   receivedFrame >= freezeFrame && processedTick >= freshFromTick;

    /// <summary>
    /// Right after this client froze (<see cref="MeasureFreeze"/>), moves this car's whole set
    /// along the rails to where the host's is, in one physics step, while the screen has only just
    /// come back (<see cref="FreezeCatchUp"/>). Left to the correction, a train 31 m behind was
    /// driven at the host's speed + 5 m/s, in view, and the full sync snapped the last 27 m.
    /// The set's first car does it, for every car in the same step (cars moved apart would be
    /// torn at their couplers). Every car must follow fresh samples of a moving host car, as
    /// coupled as the host's, and the errors known must agree: otherwise ours isn't the host's
    /// set, and it's left to the correction and the full sync as before. True if it moved them.
    /// </summary>
    private bool TryCatchUp()
    {
        List<TrainCar> cars = trainCar.trainset?.cars;
        if (cars != null && cars.Count > 0 && cars[0] != trainCar)
            return false;
        int count = cars?.Count ?? 1;
        var errors = new FreezeCatchUp();
        catchUpSet.Clear();
        for (int i = 0; i < count; i++)
        {
            TrainSpeedQueue queue = cars == null || cars[i] == trainCar ? this : cars[i] != null ? cars[i].GetComponent<TrainSpeedQueue>() : null;
            if (queue == null || !queue.Following || queue.Kinematic || queue.HostCoupled != count > 1 || !queue.HasFreshSample)
                return false;
            // Often a track ahead: the train ran on for seconds.
            if (queue.TryGetAlongTrackError(out float error, acrossJoint: true))
                errors.Add(error);
            catchUpSet.Add(queue);
        }
        if (!errors.TryGetDistance(out float metres))
            return false;
        foreach (TrainSpeedQueue queue in catchUpSet)
            queue.caughtUpAfter = freezes;
        if (!FreezeCatchUp.IsWorthMoving(metres))
            return false;
        // All or none: a bogie two track joints away isn't placed.
        foreach (TrainSpeedQueue queue in catchUpSet)
            if (!queue.TryPrepareCatchUp(metres))
                return false;
        foreach (TrainSpeedQueue queue in catchUpSet)
            queue.CatchUp();
        Multiplayer.Log($"{trainCar.ID}{(count > 1 ? $" and {count - 1} coupled" : "")}: caught up {metres:F2} m along the track " +
                        $"after a {lastFreezeSeconds:F2} s freeze (host speed {targetSpeed:F2} m/s)");
        return true;
    }

    private bool TryPrepareCatchUp(float metres)
    {
        if (bogies == null || bogies.Length == 0)
            return false;
        if (catchUpShifts.Length != bogies.Length)
            catchUpShifts = new Vector3[bogies.Length];
        for (int i = 0; i < bogies.Length; i++)
            if (bogies[i] == null || !bogies[i].TryGetShiftAlong(trainCar, metres, out catchUpShifts[i]))
                return false;
        return true;
    }

    /// <summary>
    /// Moves the car by the shifts <see cref="TryPrepareCatchUp"/> found: its bogies onto the rails
    /// further on, the body with them (turned as the line between its bogies turned, so its joints
    /// don't wrench it round on a curve), at the host's speed.
    /// </summary>
    private void CatchUp()
    {
        Rigidbody body = trainCar.rb;
        Quaternion turn = Quaternion.identity;
        if (bogies.Length == 2)
        {
            Vector3 before = bogies[1].Bogie.rb.position - bogies[0].Bogie.rb.position;
            Vector3 after = before + catchUpShifts[1] - catchUpShifts[0];
            if (before.sqrMagnitude > 0.01f && after.sqrMagnitude > 0.01f)
                turn = Quaternion.FromToRotation(before, after);
        }
        Vector3 oldMid = Vector3.zero, newMid = Vector3.zero;
        for (int i = 0; i < bogies.Length; i++)
        {
            Vector3 at = bogies[i].Bogie.rb.position;
            oldMid += at;
            newMid += at + catchUpShifts[i];
            bogies[i].ShiftAlong(catchUpShifts[i]);
        }
        oldMid /= bogies.Length;
        newMid /= bogies.Length;
        body.MovePosition(newMid + turn * (body.position - oldMid));
        body.MoveRotation(turn * body.rotation);
        body.angularVelocity = Vector3.zero;

        easedSpeed = targetSpeed;
        easedCorrection = 0f;
        driving = true;
        ApplySpeed(targetSpeed);
        holdDriveSince = Time.time;
        // The bogies' travellers stay where they were until vanilla's next step.
        correctFrom = Time.fixedTime + 2f * Time.fixedDeltaTime;
        trainCar.stress?.ResetTrainStress();
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

    private bool TryGetAlongTrackError(out float error, bool acrossJoint = false)
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
            if (bogies[i] != null && bogies[i].TryGetAlongTrackError(trainCar, out float bogieError, acrossJoint))
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
