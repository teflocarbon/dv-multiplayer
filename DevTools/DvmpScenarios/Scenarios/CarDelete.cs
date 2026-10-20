using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A car deleted twice (#84): the host bulk-deletes a loaded consist the way the yard's cleanup
    /// does and deletes each car again in the same frame. That threw inside DV's bulk delete, which
    /// stopped it with cars switched off but never deleted: they stayed in their trainsets
    /// unnetworked, and the host refused every join. Pass: no such trainset is left, and the bulk
    /// delete didn't throw.
    /// </summary>
    [Scenario("car-delete", "The host bulk-deletes a loaded consist and deletes each car again in the same frame: no trainset keeps a deleted car, and DV's bulk delete doesn't throw (#84)")]
    [Budget(180)]
    public sealed class CarDelete : Scenario<CarDelete.Options>
    {
        public sealed class Options { }

        protected override async Task Run(Pair pair, Options o)
        {
            long cursor = await pair.Host.LogCursor();
            Deleted r = await pair.Host.Run<Deleted>(s => InGame.DeleteTwice(s));
            int thrown = (await pair.Host.LogsSince(cursor, "exception"))
                .Count(l => l.Level == "exception" && (l.Message + "\n" + l.Stack).Contains("DeleteTrainCarsThroughPeriodOfTime"));
            Report.Note($"{string.Join(",", r.Cars)} on {r.Track}, {r.Sets} trainset(s), cargo {string.Join(",", r.Cargo)}");
            Expect.Empty(r.StaleSets, "trainsets left holding deleted cars");
            Expect.Zero(thrown, "times DV's bulk delete threw (DeleteTrainCarsThroughPeriodOfTime)");
        }

        public sealed class Deleted
        {
            public string Track;
            public string[] Cars, StaleSets;
            public int Sets;
            public float[] Cargo;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: a coupled consist of four loaded hoppers on an empty track near the host's player
            /// (a dedicated host's parked body), deleted twice in one frame: DV's slow bulk delete
            /// (DeleteTrainCarsFromTrack, what the yard's cleanup uses) and then DeleteCar on each car.
            /// Reports trainsets left holding a car that's gone (inactive, or without its
            /// NetworkedTrainCar), which used to refuse every join (#84).
            /// </summary>
            [Step]
            public static IEnumerator DeleteTwice(Step<Deleted> step)
            {
                var hopper = Cars.Livery("HopperBrown");
                var player = PlayerManager.PlayerTransform.position;
                var track = Object.FindObjectsOfType<RailTrack>()
                    .Where(t => Cars.Length(t) > 80 && !t.BogiesOnTrack().Any())
                    .OrderBy(t => (t.transform.position - player).sqrMagnitude).First();
                var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(Enumerable.Repeat(hopper, 4).ToList(), Enumerable.Repeat(false, 4).ToList(), track, true, true, 10.0, false);
                Cars.Record(cars);
                for (int i = 0; i < 60; i++) yield return null;
                foreach (var c in cars) c.logicCar.LoadCargo(c.logicCar.capacity, DV.ThingTypes.CargoType.Coal);
                for (int i = 0; i < 60; i++) yield return null;
                var result = new Deleted
                {
                    Track = track.name, Cars = cars.Select(c => c.ID).ToArray(), Sets = cars.Select(c => c.trainset?.id ?? -1).Distinct().Count(),
                    Cargo = cars.Select(c => c.logicCar.LoadedCargoAmount).ToArray()
                };
                CarSpawner.Instance.DeleteTrainCarsFromTrack(track);
                foreach (var c in cars) if (c != null && c.gameObject.activeInHierarchy) CarSpawner.Instance.DeleteCar(c);
                yield return new WaitForSecondsRealtime(3f);
                result.StaleSets = Trainset.allSets.Where(s => s.cars.Any(c => c == null || !c.gameObject.activeInHierarchy || c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>() == null))
                    .Select(s => s.id + ": " + string.Join(", ", s.cars.Select(c => c == null ? "null" : c.ID + (c.gameObject.activeInHierarchy ? "" : " inactive") + (c.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>() == null ? " unnetworked" : "")))).ToArray();
                step.Result = result;
            }
        }
    }
}
