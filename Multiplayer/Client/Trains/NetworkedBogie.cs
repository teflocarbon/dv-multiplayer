using DV.PointSet;
using Multiplayer.Core.Trains;
using Multiplayer.Shared.Protocol.Data.Train;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.World;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Client.Trains;

public class NetworkedBogie : TickedQueue<NetworkedBogie.MovementSnapshot>
{
    public sealed class MovementSnapshot
    {
        public BogieData BogieData { get; }
        public float SpeedMetresPerSecond { get; }
        /// <summary>When the host's next sample is due: a tick close by, up to a second far away.</summary>
        public float IntervalSeconds { get; }

        public MovementSnapshot(BogieData bogieData, float speedMetresPerSecond, float intervalSeconds)
        {
            BogieData = bogieData;
            SpeedMetresPerSecond = speedMetresPerSecond;
            IntervalSeconds = intervalSeconds;
        }
    }

    private const int MAX_FRAMES = 60;
    public Bogie Bogie { get; private set; }
    private Rigidbody rb;

    protected override void OnEnable()
    {
        StartCoroutine(WaitForBogie());
    }

    protected IEnumerator WaitForBogie()
    {
        int counter = 0;

        while (Bogie == null || rb == null && counter < MAX_FRAMES)
        {
            if (Bogie == null)
                Bogie = GetComponent<Bogie>();

            if (rb == null)
                rb = GetComponent<Rigidbody>();

            if (rb == null || Bogie == null)
            {
                counter++;
                yield return new WaitForEndOfFrame();
            }
        }

        base.OnEnable();

        if (Bogie == null)
        {
            Multiplayer.LogError($"{gameObject.name} ({Bogie?.Car?.ID}): {nameof(NetworkedBogie)} requires a {nameof(Bogie)} component on the same GameObject! Waited {counter} iterations");
        }

        // A bogie that loaded derailed has no Rigidbody until it is rerailed; Process picks it up then.
        if (rb == null && Bogie != null && !Bogie.HasDerailed)
        {
            Multiplayer.LogError($"{gameObject.name} ({Bogie?.Car?.ID}): {nameof(NetworkedBogie)} requires a {nameof(rb)} component on the same GameObject! Waited {counter} iterations");
        }
    }

    public void ReceiveSnapshot(BogieData bogieData, float speedMetresPerSecond, float intervalSeconds, uint tick)
    {
        base.ReceiveSnapshot(new MovementSnapshot(bogieData, speedMetresPerSecond, intervalSeconds), tick);
    }

    protected override void Process(MovementSnapshot movement, uint snapshotTick)
    {
        BogieData snapshot = movement.BogieData;

        // Vanilla only creates the bogie's Rigidbody when it is set up on the rails (including
        // after a rerail), so look it up again rather than caching a missing one forever.
        if (rb == null)
            rb = GetComponent<Rigidbody>();
        if (Bogie == null || rb == null || Bogie.HasDerailed)
            return;

        if (snapshot.HasDerailed)
        {
            Bogie.Derail();
            return;
        }

        RailTrack hostTrack = Bogie.track;
        if (snapshot.IncludesTrackData)
        {
            if (!NetworkedRailTrack.TryGet(snapshot.TrackNetId, out NetworkedRailTrack track))
            {
                Multiplayer.LogWarning($"NetworkedBogie.Process({identifier}) Failed to find track {snapshot.TrackNetId} for bogie: {Bogie.Car.ID}");
                return;
            }
            hostTrack = track.RailTrack;
        }
        if (hostTrack == null)
        {
            Multiplayer.LogWarning($"NetworkedBogie.Process({identifier}) No track for current bogie for bogie: {Bogie?.Car?.ID}, unable to move position!");
            return;
        }

        // A bogie crossing onto the next track reaches it a little earlier or later than the
        // host's does; it will get there on its own. Anything else (e.g. a junction thrown the
        // other way) is a real divergence, so put the bogie on the host's track.
        if (Bogie.track != hostTrack && !AreAdjacent(Bogie.track, hostTrack))
            Bogie.SetTrack(hostTrack, snapshot.PositionAlongTrack, snapshot.TrackDirection);

        // Note: moving the traveller here would be undone by vanilla's next FixedUpdate, which
        // re-projects the bogie's physical position onto the track. The sample is instead used
        // by TrainSpeedQueue to steer the car's velocity towards the host's position.
        sampleTrack = hostTrack;
        sampleSpan = snapshot.PositionAlongTrack;
        sampleDirection = snapshot.TrackDirection;
        sampleSpeed = movement.SpeedMetresPerSecond;
        sampleInterval = movement.IntervalSeconds;
        float ageSeconds = (NetworkLifecycle.Instance.Tick - (float)snapshotTick) / NetworkLifecycle.TICK_RATE;
        // Real time: the host's train moves in real time. A client below 12 fps runs its game
        // time slower (DV caps the frame's delta), and projecting in game time put the host's
        // train behind where it was by the time the next sample came: a second apart, far trains
        // fell metres behind and the next full sync snapped them (train-rate, 2026-10-20).
        sampleTime = Time.realtimeSinceStartup - Mathf.Clamp(ageSeconds, 0f, MaxSampleAgeSeconds);
        hasSample = true;
    }

    public override void Clear()
    {
        base.Clear();
        hasSample = false;
    }

    private const float MaxSampleAgeSeconds = 0.5f;
    private const float MaxHoldSampleAgeSeconds = 10f;
    private bool hasSample;
    private RailTrack sampleTrack;
    private double sampleSpan;
    private int sampleDirection;
    private float sampleSpeed;
    private float sampleInterval;
    private float sampleTime;

    /// <summary>
    /// How far (metres, along the car's forward axis) the host's bogie is ahead of ours,
    /// with the host sample projected forward to now. False when the bogies aren't on the same
    /// track (crossing a track boundary) or there is no usable sample. With
    /// <paramref name="acrossJoint"/>, a host bogie on the track joined to either end of ours is
    /// measured too (catching up after a freeze, when it's often a track ahead).
    /// </summary>
    public bool TryGetAlongTrackError(TrainCar car, out float metres, bool acrossJoint = false)
    {
        metres = 0f;
        // A stopped host car is where its last sample says: projecting nothing, it goes stale far
        // more slowly than a moving one. But the host resyncs every car every 2 s, so one with no
        // sample for much longer than that is aimed at something the host no longer has (in
        // playtest 2, a rerailed loco was placed towards a sample from before it derailed).
        // A moving one is projected until the car would be taken as lost (TrainSpeedQueue): 1 s
        // close by, longer for a far train, whose samples come up to a second apart.
        bool holding = Mathf.Abs(sampleSpeed) < TrainSpeedQueue.HoldSpeed;
        float age = Time.realtimeSinceStartup - sampleTime;
        float maxAge = holding ? MaxHoldSampleAgeSeconds
            : Mathf.Max(MaxSampleAgeSeconds * 2, MaxSampleAgeSeconds + TrainSpeedQueue.StaleSeconds(sampleInterval));
        if (!hasSample || Bogie == null || Bogie.HasDerailed || Bogie.track == null || sampleTrack == null ||
            (Bogie.track != sampleTrack && !acrossJoint) || Bogie.traveller == null || age > maxAge)
            return false;

        // Span increases along the bogie's forward when its track direction is +1.
        float bogieToCar = Mathf.Sign(Vector3.Dot(Bogie.transform.forward, car.transform.forward));
        double hostSpan = holding ? sampleSpan
            : sampleSpan + sampleDirection * bogieToCar * sampleSpeed * age;
        if (Bogie.track != sampleTrack)
        {
            double length = Bogie.traveller.pointSet.span, sampleLength = sampleTrack.GetKinkedPointSet().span;
            Junction.Branch outBranch = Bogie.track.GetOutBranch(), inBranch = Bogie.track.GetInBranch();
            if (outBranch?.track == sampleTrack)
                hostSpan = TrackSpans.FromNext(hostSpan, length, atEnd: true, outBranch.first, sampleLength);
            else if (inBranch?.track == sampleTrack)
                hostSpan = TrackSpans.FromNext(hostSpan, length, atEnd: false, inBranch.first, sampleLength);
            else
                return false;
        }
        metres = (float)(hostSpan - Bogie.traveller.Span) * Bogie.TrackDirectionSign * bogieToCar;
        return true;
    }

    /// <summary>
    /// The world shift that puts this (dynamic) bogie <paramref name="metres"/> further along the
    /// car's forward axis on the rails, at most one track joint away. Vanilla's next step takes
    /// the move up into the bogie's traveller as if it had run there (projecting it onto the rails
    /// it stood on and crossing onto the next track), so only the body is moved, not the traveller.
    /// </summary>
    public bool TryGetShiftAlong(TrainCar car, float metres, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (Bogie == null || Bogie.HasDerailed || Bogie.rb == null || Bogie.rb.isKinematic ||
            Bogie.track == null || Bogie.traveller == null)
            return false;
        float bogieToCar = Mathf.Sign(Vector3.Dot(Bogie.transform.forward, car.transform.forward));
        RailTrack track = Bogie.track;
        double length = Bogie.traveller.pointSet.span;
        double span = Bogie.traveller.Span + metres * Bogie.TrackDirectionSign * bogieToCar;
        if (span < 0 || span >= length)
        {
            // Only onto the track the host's bogie is on: a junction thrown since the host's
            // train passed would otherwise send ours down the other branch.
            Junction.Branch branch = span < 0 ? track.GetInBranch() : track.GetOutBranch();
            if (branch?.track == null || !hasSample || branch.track != sampleTrack)
                return false;
            track = branch.track;
            if (!TrackSpans.TryCross(span, length, branch.first, track.GetKinkedPointSet().span, out span))
                return false;
        }
        var traveller = new PointSetTraveller(track.GetKinkedPointSet());
        traveller.MoveToSpan(span);
        shift = (Vector3)traveller.worldPosition + WorldMover.currentMove - Bogie.rb.position;
        return true;
    }

    /// <summary>Moves the bogie by a shift from <see cref="TryGetShiftAlong"/>.</summary>
    public void ShiftAlong(Vector3 shift) => Bogie.rb.MovePosition(Bogie.rb.position + shift);

    /// <summary>
    /// Moves a kinematic bogie <paramref name="metres"/> along the car's forward axis on its track
    /// (vanilla doesn't project a kinematic bogie onto the track, so move the traveller too).
    /// <paramref name="shift"/> is how far it moved in the world, for the car body to follow.
    /// </summary>
    public bool TryPlaceAlong(TrainCar car, float metres, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (Bogie == null || Bogie.HasDerailed || Bogie.traveller == null || Bogie.rb == null)
            return false;
        float bogieToCar = Mathf.Sign(Vector3.Dot(Bogie.transform.forward, car.transform.forward));
        double span = Bogie.traveller.Span + metres * Bogie.TrackDirectionSign * bogieToCar;
        // Only along this track: crossing onto the next is vanilla's job (UpdatePointSetTraveller).
        if (span < 0 || span >= Bogie.traveller.pointSet.span)
            return false;
        Vector3d before = Bogie.traveller.worldPosition;
        // MoveToSpan, not Travel: Travel(offset, 0) leaves the traveller where it is.
        Bogie.traveller.MoveToSpan(span);
        // Vanilla advances the traveller by the bogie's movement since point1/point2 (the track
        // points it last stood between). Left stale, the first dynamic step after placing measured
        // a large false move, jumped the traveller and threw the bogie there (overshoot, launch).
        Bogie.point1 = Bogie.traveller.curPoint;
        int next = Bogie.traveller.curPoint.index + 1;
        Bogie.point2 = Bogie.traveller.pointSet.points[next < Bogie.traveller.pointSet.points.Length ? next : next - 1];
        Vector3d after = Bogie.traveller.worldPosition;
        shift = new Vector3((float)(after.x - before.x), (float)(after.y - before.y), (float)(after.z - before.z));
        Bogie.rb.MovePosition(Bogie.rb.position + shift);
        return true;
    }

    private static bool AreAdjacent(RailTrack a, RailTrack b) =>
        a != null && b != null &&
        (a.GetInBranch()?.track == b || a.GetOutBranch()?.track == b ||
         b.GetInBranch()?.track == a || b.GetOutBranch()?.track == a);
}
