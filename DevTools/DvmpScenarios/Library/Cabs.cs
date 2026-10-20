// Steps the loco and cab scenarios share (cab-controls, settle-window, stoker-coal,
// compressor-signal, fps-drop), and the plain data they pass. Each [Step] runs inside a game: call
// it through pair.Host / pair.Client / pair.Both, never directly.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dvmp.Lib;
using HarmonyLib;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>A loco a scenario spawned (Cars.SpawnLoco): the loco, then the rest of its trainset.</summary>
    public sealed class Loco
    {
        public string Id;
        public string[] Cars;
        public int NetId;
        public Abs At;
    }

    /// <summary>A sim port's value on one game; -1 when the car has no such port.</summary>
    public sealed class PortValue
    {
        public string Port;
        public float Value;
        public override string ToString() => Port + " " + Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>The host's values for some ports of a loco, and how many control leases it still holds.</summary>
    public sealed class HostPorts
    {
        public PortValue[] Values;
        public int Leases;
    }

    /// <summary>How many packets of a type the server handled from one sender ("host": the host's own
    /// player; "client": anyone else) about one car.</summary>
    public sealed class PacketCount
    {
        public string Packet, From;
        public int Car, N;
    }

    public static partial class Cars
    {
        /// <summary>
        /// Host: a <paramref name="livery"/> near the host's player, left 120 frames to land. With
        /// <paramref name="handbrake"/>, its handbrake on. With <paramref name="tender"/>, a steam
        /// loco comes with its tender coupled (Trains.SpawnWithTenderNearPlayer, both braked): spawning
        /// one car never brings it, and without it there's no coal pile. Not watched for creep: for a
        /// loco that must stay put under a boarded client, Cars.SpawnHeldLoco.
        /// </summary>
        [Step]
        public static IEnumerator SpawnLoco(Step<Loco> step, string livery, bool handbrake, bool tender)
        {
            TrainCar car = tender ? Trains.SpawnWithTenderNearPlayer(livery) : Trains.SpawnNearPlayer(livery);
            foreach (TrainCar c in car.trainset.cars) spawned.Add(c.ID);
            if (handbrake) car.brakeSystem.SetHandbrakePosition(1f);
            for (int i = 0; i < 120; i++) yield return null;
            if (tender) yield return Trains.CoupleTender(car);
            foreach (TrainCar c in car.trainset.cars) spawned.Add(c.ID);
            step.Result = new Loco
            {
                Id = car.ID,
                Cars = new[] { car }.Concat(car.trainset.cars.Where(c => c != car)).Select(c => c.ID).ToArray(),
                NetId = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>().NetId,
                At = Positions.Of(car.transform.position)
            };
        }
    }

    public static class Cabs
    {
        /// <summary>This game's values for the loco's ports (-1 for a port it doesn't have).</summary>
        [Step]
        public static PortValue[] Values(string loco, string[] ports) => Read(Trains.Find(loco), ports);

        /// <summary>Host: the loco's values for the ports, and how many control leases are still held
        /// on it (none should be, once every interaction is over).</summary>
        [Step]
        public static HostPorts Host(string loco, string[] ports)
        {
            TrainCar car = Trains.Find(loco);
            var ntc = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>();
            return new HostPorts { Values = Read(car, ports), Leases = Multiplayer.Server.Trains.NetworkedTrainCarServer.Of(ntc).LeaseCount };
        }

        /// <summary>This game's values for the ports, rounded as the checks compare them.</summary>
        internal static PortValue[] Read(TrainCar car, IEnumerable<string> ports)
        {
            var flow = car.GetComponent<Multiplayer.Shared.Trains.NetworkedTrainCar>().simulationFlow;
            return ports.Distinct().Select(p => new PortValue { Port = p, Value = flow.TryGetPort(p, out var port) ? (float)Math.Round(port.Value, 3) : -1 }).ToArray();
        }

        /// <summary>
        /// In the orchestrator: the client's values for some ports (as a step read them, after a
        /// change) against the host's. Fails on any port 0.01 or more apart, or a lease still held.
        /// </summary>
        public static async Task<bool> ExpectHostAgrees(Pair pair, string loco, PortValue[] client, string what)
        {
            string[] ports = client.Select(v => v.Port).ToArray();
            HostPorts host = await pair.Host.Run(() => Host(loco, ports));
            var differ = client
                .Select(c => (c, h: host.Values.First(v => v.Port == c.Port)))
                .Where(x => Math.Abs(x.c.Value - x.h.Value) > 0.01)
                .Select(x => $"{x.c.Port} client {x.c.Value.ToString(CultureInfo.InvariantCulture)} host {x.h.Value.ToString(CultureInfo.InvariantCulture)}")
                .ToList();
            return Expect.That(differ.Count == 0 && host.Leases == 0, what,
                $"{client.Length} port(s)" + (differ.Count > 0 ? ", differ: " + string.Join("; ", differ) : ", all match") +
                (host.Leases > 0 ? $", {host.Leases} lease(s) still held" : ""));
        }
    }

    /// <summary>
    /// Host: counts the packets the server handles, by type, sender and car. Was count-sends.cs,
    /// which counted NetworkClient's send calls on each game; stage 3 removes the host's client, so
    /// the server counts what reaches it instead: a packet from the host's own client arrives from
    /// the host's own player ("host"), every other from "client".
    /// </summary>
    public static class Received
    {
        private static readonly Dictionary<string, PacketCount> counts = new();
        private static readonly HashSet<string> patched = new();
        private static bool counting;

        /// <summary>Host: starts counting the packets of these types (the packet class's name, e.g.
        /// ServerboundAddCoalPacket) from zero. Each handler stays patched for the life of the game.</summary>
        [Step]
        public static void Count(string[] packets)
        {
            var harmony = new Harmony("dvmp.scenarios.received." + typeof(Received).Assembly.GetName().Name);
            foreach (string packet in packets.Where(p => !patched.Contains(p)))
            {
                MethodInfo handler = typeof(Multiplayer.Server.Session.NetworkServer).GetMethod("On" + packet, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Exception("no NetworkServer.On" + packet);
                harmony.Patch(handler, prefix: new HarmonyMethod(typeof(Received).GetMethod(nameof(Seen), BindingFlags.NonPublic | BindingFlags.Static)));
                patched.Add(packet);
            }
            counts.Clear();
            counting = true;
        }

        /// <summary>Host: the counts so far of these packet types.</summary>
        [Step]
        public static PacketCount[] Counts(string[] packets) => counts.Values.Where(c => packets.Contains(c.Packet)).ToArray();

        private static void Seen(object[] __args)
        {
            if (!counting || __args.Length < 2 || __args[0] == null) return;
            object packet = __args[0];
            Type t = packet.GetType();
            object netId = t.GetField("NetId")?.GetValue(packet) ?? t.GetProperty("NetId")?.GetValue(packet);
            var server = Multiplayer.Server.Session.NetworkServer.Instance;
            string from = server.TryGetServerPlayer((Multiplayer.Shared.Transport.ITransportPeer)__args[1], out var player) && player == server.LocalPlayer ? "host" : "client";
            int car = netId == null ? 0 : Convert.ToInt32(netId);
            string key = t.Name + ":" + from + ":" + car;
            if (!counts.TryGetValue(key, out PacketCount c))
                counts[key] = c = new PacketCount { Packet = t.Name, From = from, Car = car };
            c.N++;
        }
    }
}
