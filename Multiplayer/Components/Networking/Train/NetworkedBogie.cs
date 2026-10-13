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

        //Multiplayer.LogDebug(()=>$"NetworkedBogie.Process({identifier}) DataFlags: {snapshot.DataFlags}, {snapshotTick}, {snapshot.TrackNetId}, {snapshot.PositionAlongTrack} {snapshot.TrackDirection}");

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

        if (snapshot.IncludesTrackData)
        {
            if (!NetworkedRailTrack.TryGet(snapshot.TrackNetId, out NetworkedRailTrack track))
            {
                Multiplayer.LogWarning($"NetworkedBogie.Process({identifier}) Failed to find track {snapshot.TrackNetId} for bogie: {Bogie.Car.ID}");
                return;
            }

            if (Bogie.track != track.RailTrack)
                Bogie.SetTrack(track.RailTrack, snapshot.PositionAlongTrack, snapshot.TrackDirection);
            else
                Bogie.traveller.MoveToSpan(snapshot.PositionAlongTrack);
        }
        else
        {
            if (Bogie.track)
                Bogie.traveller.MoveToSpan(snapshot.PositionAlongTrack);
            else
                Multiplayer.LogWarning($"NetworkedBogie.Process({identifier}) No track for current bogie for bogie: {Bogie?.Car?.ID}, unable to move position!");
        }

        // Project the timestamped host sample to the current network tick. Do not use
        // the client's locally simulated rigidbody velocity here: it may be stale.
        int physicsSteps = Mathf.Max(1, Mathf.FloorToInt(
            (NetworkLifecycle.Instance.Tick - (float)snapshotTick) /
            NetworkLifecycle.TICK_RATE / Time.fixedDeltaTime) + 1);
        for (int i = 0; i < physicsSteps; i++)
            Bogie.UpdatePointSetTraveller(movement.SpeedMetresPerSecond);
    }
}
