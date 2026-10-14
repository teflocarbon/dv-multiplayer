using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Train;
using System.Collections;
using UnityEngine;

namespace Multiplayer.Components.Networking.Train;

public class NetworkedBogie : TickedQueue<NetworkedBogie.MovementSnapshot>
{
    public sealed class MovementSnapshot
    {
        public BogieData BogieData { get; }
        public float SpeedMetresPerSecond { get; }

        public MovementSnapshot(BogieData bogieData, float speedMetresPerSecond)
        {
            BogieData = bogieData;
            SpeedMetresPerSecond = speedMetresPerSecond;
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

    public void ReceiveSnapshot(BogieData bogieData, float speedMetresPerSecond, uint tick)
    {
        base.ReceiveSnapshot(new MovementSnapshot(bogieData, speedMetresPerSecond), tick);
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
        float ageSeconds = (NetworkLifecycle.Instance.Tick - (float)snapshotTick) / NetworkLifecycle.TICK_RATE;
        sampleTime = Time.time - Mathf.Clamp(ageSeconds, 0f, MaxSampleAgeSeconds);
        hasSample = true;
    }

    public override void Clear()
    {
        base.Clear();
        hasSample = false;
    }

    private const float MaxSampleAgeSeconds = 0.5f;
    private bool hasSample;
    private RailTrack sampleTrack;
    private double sampleSpan;
    private int sampleDirection;
    private float sampleSpeed;
    private float sampleTime;

    /// <summary>
    /// How far (metres, along the car's forward axis) the host's bogie is ahead of ours,
    /// with the host sample projected forward to now. False when the bogies aren't on the same
    /// track (crossing a track boundary) or there is no usable sample.
    /// </summary>
    public bool TryGetAlongTrackError(TrainCar car, out float metres)
    {
        metres = 0f;
        // A stopped host car is where its last sample says, however old that is: projecting
        // nothing, it can't go stale. A moving sample can.
        bool holding = Mathf.Abs(sampleSpeed) < TrainSpeedQueue.HoldSpeed;
        if (!hasSample || Bogie == null || Bogie.HasDerailed || Bogie.track != sampleTrack ||
            Bogie.traveller == null || (!holding && Time.time - sampleTime > MaxSampleAgeSeconds * 2))
            return false;

        // Span increases along the bogie's forward when its track direction is +1.
        float bogieToCar = Mathf.Sign(Vector3.Dot(Bogie.transform.forward, car.transform.forward));
        double hostSpan = holding ? sampleSpan
            : sampleSpan + sampleDirection * bogieToCar * sampleSpeed * (Time.time - sampleTime);
        metres = (float)(hostSpan - Bogie.traveller.Span) * Bogie.TrackDirectionSign * bogieToCar;
        return true;
    }

    private static bool AreAdjacent(RailTrack a, RailTrack b) =>
        a != null && b != null &&
        (a.GetInBranch()?.track == b || a.GetOutBranch()?.track == b ||
         b.GetInBranch()?.track == a || b.GetOutBranch()?.track == a);
}
