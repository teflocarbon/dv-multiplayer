// Shared probe library: trains. Spawn a car or loco near the players (host), board a cab like a
// player stepping in, push a consist along the track, and clean up. Coroutine helpers are used
// with `yield return Trains.Board(car);`.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dvmp.Agent;
using UnityEngine;

namespace Dvmp.Lib
{
    public static class Trains
    {
        public static TrainCar Find(string id) =>
            UnityEngine.Object.FindObjectsOfType<TrainCar>().FirstOrDefault(c => c.ID == id)
            ?? throw new InvalidOperationException("no car " + id);

        /// <summary>Host only: spawns a car of the given livery on the track nearest a point
        /// <paramref name="ahead"/> metres in front of the local player.</summary>
        public static TrainCar SpawnNearPlayer(string liveryId, float ahead = 25f)
        {
            var livery = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == liveryId)
                ?? throw new InvalidOperationException("no livery " + liveryId);
            // The nearest track can be occupied; try points around the player until one has room.
            Transform player = PlayerManager.PlayerTransform;
            Vector3 flatForward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, flatForward);
            foreach (float distance in new[] { ahead, ahead * 1.6f, ahead * 2.4f })
            foreach (Vector3 dir in new[] { flatForward, right, -right, -flatForward })
            {
                TrainCar car = CarSpawner.Instance.SpawnCarOnClosestTrack(player.position + dir * distance, livery, false, true, false);
                if (car != null)
                    return car;
            }
            throw new InvalidOperationException("no room to spawn " + liveryId + " near the player");
        }

        public static void Delete(TrainCar car)
        {
            if (car != null)
                CarSpawner.Instance.DeleteCar(car);
        }

        /// <summary>A point on the floor inside the car's cab (locos), or on its deck.</summary>
        public static Vector3 FloorPoint(TrainCar car)
        {
            Transform root = car.interior != null ? car.interior : car.transform;
            int dampening = LayerMask.NameToLayer("Camera_Dampening");
            var cab = root.GetComponentsInChildren<Collider>(true).Where(c => c.gameObject.layer == dampening).ToList();
            Vector3 centre;
            if (cab.Count > 0)
            {
                Bounds b = cab[0].bounds;
                foreach (Collider c in cab.Skip(1)) b.Encapsulate(c.bounds);
                centre = b.center;
            }
            else
                centre = car.transform.position + Vector3.up * 3f;
            // Cast from inside the cab volume (below its roof) down to the floor.
            int walkable = LayerMask.GetMask("Train_Walkable", "Train_Interior");
            if (Physics.Raycast(centre, Vector3.down, out RaycastHit hit, 6f, walkable, QueryTriggerInteraction.Ignore))
                return hit.point;
            return centre;
        }

        private static bool HasCab(TrainCar car)
        {
            if (car.interior == null)
                return false;
            int dampening = LayerMask.NameToLayer("Camera_Dampening");
            return car.interior.GetComponentsInChildren<Collider>(true).Any(c => c.gameObject.layer == dampening);
        }

        /// <summary>Puts the local player on the car's floor and attaches them to the car the way the
        /// game does when a player steps on (a teleport alone doesn't attach them).</summary>
        public static IEnumerator Board(TrainCar car)
        {
            // A loco's cab interior only loads with a player nearby; without it the floor search
            // falls back to a ray from above and lands the player on the roof. Get close, wait for it.
            if (car.IsLoco)
            {
                if (Vector3.Distance(PlayerManager.PlayerTransform.position, car.transform.position) > 15f)
                    yield return LocalPlayer.TeleportAbs(Dev.Abs(car.transform.position + car.transform.right * 4f), 0.5f);
                float deadline = Time.realtimeSinceStartup + 15f;
                while (!HasCab(car) && Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (!HasCab(car))
                    throw new InvalidOperationException("cab interior of " + car.ID + " did not load");
            }
            Vector3 floor = FloorPoint(car);
            if (car.IsLoco && car.transform.InverseTransformPoint(floor).y > 2.4f)
                throw new InvalidOperationException("cab floor of " + car.ID + " not found (would board onto the roof)");
            yield return LocalPlayer.TeleportAbs(Dev.Abs(floor) + Vector3.up * 0.1f, 0.5f);
            Type reparentType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "DV.CharacterController")
                .SelectMany(a => a.GetTypes()).First(t => t.Name == "CharacterReparenting");
            UnityEngine.Object reparenting = UnityEngine.Object.FindObjectOfType(reparentType);
            reparentType.GetMethod("ReparentTo").Invoke(reparenting, new object[] { car.transform, false, null });
            // A moving train travels on while the teleport settles; now that the player rides with
            // the car, put them back on its floor (in the car's current frame).
            for (int i = 0; i < 5; i++)
            {
                PlayerManager.PlayerTransform.position = FloorPoint(car) + Vector3.up * 0.1f;
                Physics.SyncTransforms();
                yield return null;
            }
            for (int i = 0; i < 20; i++) yield return null;
            if (PlayerManager.Car != car)
                throw new InvalidOperationException("player did not attach to " + car.ID);
            float off = Vector3.Distance(PlayerManager.PlayerTransform.position, FloorPoint(car));
            if (off > 3f)
                throw new InvalidOperationException($"player ended {off:F1} m from the floor of {car.ID}");
        }

        /// <summary>Host: pushes the car's whole consist along the track - ramp to
        /// <paramref name="speed"/> m/s, hold, ramp back to a stop - by driving the rigidbodies.</summary>
        public static IEnumerator Push(TrainCar car, float speed, float holdSeconds, float rampSeconds = 3f)
        {
            List<TrainCar> cars = car.trainset.cars.ToList();
            foreach (TrainCar c in cars) c.brakeSystem?.SetHandbrakePosition(0f);
            float total = rampSeconds * 2 + holdSeconds, t0 = Time.time;
            while (Time.time - t0 < total)
            {
                yield return new WaitForFixedUpdate();
                float t = Time.time - t0;
                float v = speed * (t < rampSeconds ? t / rampSeconds : t > rampSeconds + holdSeconds ? (total - t) / rampSeconds : 1f);
                Vector3 forward = car.transform.forward;
                foreach (TrainCar c in cars)
                {
                    Vector3 dir = c.transform.forward * Mathf.Sign(Vector3.Dot(c.transform.forward, forward));
                    c.rb.AddForce(dir * v - Vector3.Project(c.rb.velocity, dir), ForceMode.VelocityChange);
                }
            }
            for (int i = 0; i < 50; i++)
            {
                yield return new WaitForFixedUpdate();
                foreach (TrainCar c in cars) c.rb.velocity = Vector3.zero;
            }
        }
    }
}
