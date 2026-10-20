// Jobs, stations and the places the world/jobs/players scenarios go: steps two or more of them
// share. Each [Step] runs inside a game: call it through pair.Host / pair.Client.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Jobs;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>A job taken at a validator (Jobs.Accept).</summary>
    public sealed class Accepted
    {
        public string State;
        public bool OverviewGone;
        /// <summary>The job's booklets' net IDs, "(old)" after one that was there before.</summary>
        public string Booklets;
    }

    public static partial class Jobs
    {
        /// <summary>Host: clears payable debts. Test saves pile them up, and vanilla then refuses jobs.</summary>
        [Step]
        public static void ClearDebts()
        {
            var debts = DV.ServicePenalty.CareerManagerDebtController.Instance;
            debts.ClearRestOfThePayableDebts();
            debts.RefreshExistingDebtsState();
        }

        /// <summary>
        /// Either player: takes the job's overview and puts it in the nearest job validator, as
        /// accepting a job in the game does, and waits for the job to start.
        /// </summary>
        [Step]
        public static IEnumerator Accept(Step<Accepted> step, string jobId)
        {
            var nj = Resources.FindObjectsOfTypeAll<NetworkedJob>().First(n => n.Job?.ID == jobId);
            var job = nj.Job;
            var overviewItem = nj.JobOverview ?? throw new Exception("no overview for " + job.ID);
            var validator = UnityEngine.Object.FindObjectsOfType<JobValidator>().OrderBy(v => Vector3.Distance(v.transform.position, PlayerManager.PlayerTransform.position)).First();
            if (Hands.Held != null && Hands.Held.NetId != overviewItem.NetId) yield return Hands.Drop();
            if (Hands.Held == null) yield return Hands.Pickup(overviewItem.NetId);
            for (int i = 0; i < 20; i++) yield return null;
            var overview = overviewItem.GetComponent<JobOverview>();
            var before = new HashSet<JobBooklet>(UnityEngine.Object.FindObjectsOfType<JobBooklet>());
            validator.ProcessJobOverview(overview);
            for (float t0 = Time.realtimeSinceStartup; job.State == DV.ThingTypes.JobState.Available && Time.realtimeSinceStartup - t0 < 10f;) yield return null;
            for (int i = 0; i < 120; i++) yield return null;
            Hands.ReleaseControl();
            var booklets = UnityEngine.Object.FindObjectsOfType<JobBooklet>().Where(b => b.job == job).ToList();
            step.Result = new Accepted
            {
                State = job.State.ToString(),
                OverviewGone = overviewItem == null || !overviewItem.gameObject.activeInHierarchy,
                Booklets = string.Join(",", booklets.Select(b => (b.GetComponent<NetworkedItem>()?.NetId ?? 0) + (before.Contains(b) ? "(old)" : "")))
            };
        }

        /// <summary>The shared wallet, as this game shows it.</summary>
        [Step]
        public static double Wallet() => DV.InventorySystem.Inventory.Instance.PlayerMoney;
    }

    public static partial class Cars
    {
        /// <summary>In a game: records a car a scenario's own step spawned, so the leak check deletes it
        /// after the run, even one a timed-out step left behind.</summary>
        public static void Spawned(TrainCar car) => spawned.Add(car.ID);

        /// <summary>Host: one car of the livery on the track nearest the client's player (a dedicated
        /// host has no player of its own), recorded for the leak check. Result: its ID.</summary>
        [Step]
        public static IEnumerator SpawnNearClient(Step<string> step, string livery)
        {
            Vector3 near = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().transform.position;
            var l = DV.Globals.G.Types.Liveries.FirstOrDefault(x => x.id == livery) ?? throw new ArgumentException("no livery " + livery);
            TrainCar car = null;
            // The nearest track can be occupied: points around the player until one has room.
            foreach (float d in new[] { 25f, 40f, 60f })
            foreach (Vector3 dir in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left })
                if (car == null) car = CarSpawner.Instance.SpawnCarOnClosestTrack(near + dir * d, l, false, true, false);
            if (car == null) throw new Exception("no room to spawn " + livery + " near the client");
            spawned.Add(car.ID);
            for (int i = 0; i < 120; i++) yield return null;
            step.Result = car.ID;
        }
    }

    public static partial class Players
    {
        /// <summary>This game's player onto the ground at an absolute x/z, then
        /// <paramref name="seconds"/> there (the world around it streams in, stations notice).</summary>
        [Step]
        public static IEnumerator Visit(float x, float z, float seconds)
        {
            yield return LocalPlayer.TeleportToGround(x, z);
            yield return new WaitForSeconds(seconds);
        }

        /// <summary>Drops whatever this game's player holds and gives the game its input back.</summary>
        [Step]
        public static IEnumerator EmptyHands()
        {
            if (Hands.Held != null) yield return Hands.Drop();
            Hands.ReleaseControl();
        }
    }

    /// <summary>Where the scenarios send the players, and moving them there. Runs in the orchestrator.</summary>
    public static class Places
    {
        /// <summary>Food Factory (FM)'s station office: its job validator, bin and overviews. The host
        /// stands at (5990, 6740), the client at (5986, 6740).</summary>
        public const float OfficeX = 5990f, OfficeZ = 6740f;

        /// <summary>2.6 km from FM's office: a station drops its jobs, and generates new ones, once
        /// every player has been more than 2 km away (StationController.Update).</summary>
        public const float AwayX = 8600f, AwayZ = 6740f;

        /// <summary>The station with a shop, a service station and its cash register: the client
        /// stands at (13421, 3618), the host at (13424, 3622).</summary>
        public const float ShopX = 13421f, ShopZ = 3618f;

        /// <summary>Both players to FM's office, then <paramref name="seconds"/> there. A dedicated
        /// host has no player to move.</summary>
        public static Task Office(Pair pair, float seconds) => Both(pair, OfficeX, OfficeZ, OfficeX - 4f, OfficeZ, seconds);

        /// <summary>Both players 2.6 km from FM, one after the other, 8 s each, so DV drops its jobs.</summary>
        public static async Task Away(Pair pair)
        {
            if (!pair.Dedicated) await pair.Host.Run(() => Players.Visit(AwayX, AwayZ, 8f), 90);
            await pair.Client.Run(() => Players.Visit(AwayX, AwayZ, 8f), 90);
        }

        /// <summary>Both players by the shop's station.</summary>
        public static Task Shop(Pair pair, float seconds) => Both(pair, ShopX + 3f, ShopZ + 4f, ShopX, ShopZ, seconds);

        /// <summary>The host's player (not on a dedicated host) and the client's to absolute x/z.</summary>
        public static async Task Both(Pair pair, float hostX, float hostZ, float clientX, float clientZ, float seconds)
        {
            if (!pair.Dedicated) await pair.Host.Run(() => Players.Visit(hostX, hostZ, seconds), 90);
            await pair.Client.Run(() => Players.Visit(clientX, clientZ, seconds), 90);
        }

        /// <summary>
        /// The peer an option names ("host" or "client") as the player who acts. A dedicated host
        /// has no player: that's a run the pair can't do, a harness error, as a scenario's
        /// [Needs(Need.PlayerHost)] is.
        /// </summary>
        public static Peer Player(Pair pair, string who, string option)
        {
            if (who == "client") return pair.Client;
            if (who != "host") throw new HarnessException($"{option}={who}: host or client");
            if (pair.Dedicated) throw new HarnessException($"{option}=host needs a player host; the session's host is dedicated");
            return pair.Host;
        }
    }
}
