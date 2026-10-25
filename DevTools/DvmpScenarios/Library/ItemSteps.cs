// Item steps more than one item scenario uses: where an item is in a game and in the server's
// record, carrying one into a cab, personal items, Lost and Found. Each [Step] runs inside a game:
// call it through pair.Host / pair.Client. The framework's own item steps are in Items.cs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Items.Physics;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>A point in a car's own frame (its transform), comparable across games.</summary>
    public sealed class Point
    {
        public float X, Y, Z;
        public override string ToString() => FormattableString.Invariant($"({X:0.##}, {Y:0.##}, {Z:0.##})");
        public static float Distance(Point a, Point b) => Mathf.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }

    /// <summary>Where a game has an item (Items.Pose).</summary>
    public sealed class ItemPose
    {
        public bool Present;
        public Abs Pos;
        public string State;
        public uint Revision;
        /// <summary>A spatial lease is open on it in this game.</summary>
        public bool Leased;
        /// <summary>The Unity object's instance ID: a copy retired and re-created is another one.</summary>
        public int Instance;
        /// <summary>How far under this game's terrain it is (negative above), when there's terrain under it.</summary>
        public float Depth;
        public bool HasTerrain;
        public override string ToString() => Present ? $"{Pos} r{Revision} {State}" : "missing";
    }

    /// <summary>The server's record of an item and its Lost and Found state (Items.Record, host).</summary>
    public sealed class ItemRecord
    {
        public bool Known;
        public string Placement = "";
        public int Owner = -1, PlacementPlayer = -1;
        public uint Revision;
        public string ParentKind = "";
        public int ParentNetId;
        public Abs Pos;
        public string LastMove = "", LastMoveDetail = "";
        public bool Lost, Exists;
        /// <summary>The item's handle, what a list row names it by (wire.md, "Naming an item").</summary>
        public uint Handle;
        /// <summary>The client's player number (the first remote avatar) and the host's own (0 for none).</summary>
        public int ClientId, HostId;
        public override string ToString() => Known ? $"{Placement} (player {PlacementPlayer}, owner {Owner}, r{Revision}{(Lost ? ", lost" : "")})" : "no record";
    }

    /// <summary>Where a game has an item relative to a car (Items.OnCar).</summary>
    public sealed class ItemOnCar
    {
        public bool Present;
        /// <summary>Attached to the car (TrainCar.Resolve of its transform).</summary>
        public bool InCar;
        public Point Local;
        public string State;
        public int CarNetId;
    }

    public static partial class Items
    {
        /// <summary>
        /// Host: a fresh personal item of <paramref name="prefab"/> beside a player
        /// (<paramref name="near"/> "host" or "client"), the way a shop purchase makes one: the host
        /// instantiates it and marks it as belonging to the player. Waits for the client's avatar
        /// (it may still be joining). Result: its net ID.
        /// </summary>
        [Step]
        public static IEnumerator SpawnPersonal(Step<ushort> step, string prefab, string near)
        {
            Multiplayer.Shared.Players.NetworkedPlayer client = null;
            yield return Wait.Until(() => (client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().FirstOrDefault()) != null, 60f, "the client's avatar");
            Vector3 at = near == "host" ? PlayerManager.PlayerTransform.position : client.transform.position;
            var spec = DV.Globals.G.Items.items.FirstOrDefault(i => i.ItemPrefabName == prefab)
                ?? throw new ArgumentException("no item spec with prefab " + prefab);
            var go = UnityEngine.Object.Instantiate(spec.gameObject, at + Vector3.up * 0.6f + Vector3.right * 0.8f, Quaternion.identity, WorldMover.OriginShiftParent);
            go.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
            NetworkedItem item = go.GetComponent<NetworkedItem>();
            yield return Wait.Until(() => item != null && item.NetId != 0, 5f, $"the {prefab} to get a net ID");
            spawned.Add(item.NetId);
            for (int i = 0; i < 60; i++) yield return null;
            step.Result = item.NetId;
        }

        /// <summary>Yields until this game has the item (a client may still be creating its copy);
        /// throws after <paramref name="seconds"/>.</summary>
        public static IEnumerator Arrive(ushort netId, float seconds = 20f) =>
            Wait.Until(() => NetworkedItem.TryGet(netId, out NetworkedItem n) && n != null, seconds, $"item {netId} to arrive");

        /// <summary>
        /// Fetches the item, boards the loco's cab with it and drops or throws it there
        /// (<paramref name="release"/> "drop" or "throw", the throw at the floor: one out of the open
        /// cab is physics, not sync), then waits <paramref name="framesAfter"/> frames. Result: the
        /// seconds the cab interior took to load after boarding.
        /// </summary>
        [Step]
        public static IEnumerator CarryIntoCab(Step<float> step, string loco, ushort netId, string release, int framesAfter)
        {
            TrainCar car = Trains.Find(loco);
            yield return Arrive(netId);
            NetworkedItem.TryGet(netId, out NetworkedItem item);
            if (Hands.Held != null) yield return Hands.Drop();   // leftovers from an earlier run
            yield return LocalPlayer.GoTo(item);
            yield return Hands.Pickup(netId);
            yield return Trains.Board(car);
            // DV loads the cab's interior once the player is in it, which can take seconds. An item
            // released before it exists has no cab to land in: it settles in the world and stays
            // behind when the train moves (a run threw 2.8 s after boarding, with "failed to load an
            // interior" logged).
            float boarded = Time.realtimeSinceStartup;
            while (car.loadedInterior == null && Time.realtimeSinceStartup - boarded < 20f) yield return null;
            if (car.loadedInterior == null) throw new Exception("the cab interior never loaded in 20 s: not an item failure");
            step.Result = Time.realtimeSinceStartup - boarded;
            for (int i = 0; i < 20; i++) yield return null;
            if (release == "throw") yield return Hands.Throw(car.transform.forward * 0.6f - Vector3.up * 0.3f);
            else yield return Hands.Drop();
            for (int i = 0; i < framesAfter; i++) yield return null;
        }

        /// <summary>Walks to the item, picks it up and puts it in the inventory (or keeps it in the
        /// hand, <paramref name="hand"/>). Result: its inventory slot (-1 in the hand).</summary>
        [Step]
        public static IEnumerator Stash(Step<int> step, ushort netId, bool hand)
        {
            yield return Arrive(netId);
            NetworkedItem.TryGet(netId, out NetworkedItem item);
            if (Hands.Held != null) yield return Hands.Drop();
            yield return LocalPlayer.GoTo(item);
            yield return Hands.Pickup(netId);
            if (!hand) DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
            for (int i = 0; i < 90; i++) yield return null;
            // Put away, a remote client keeps a stand-in and no object (D14); the host its object.
            if (hand ? item == null || item.GetItemState() != ItemState.InHand : !PlayerInventory.Holds(netId))
                throw new Exception("not where it should be: " + (item != null ? item.GetItemState().ToString() : "no object, no stand-in"));
            step.Result = PlayerInventory.Slot(netId);
        }

        /// <summary>Where this game has the item: position, state, revision, an open lease, and how
        /// far under this game's ground.</summary>
        [Step]
        public static ItemPose Pose(ushort netId)
        {
            if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null) return new ItemPose();
            float? depth = ItemFallRescue.BelowTerrain(item.transform.position, out _);
            return new ItemPose
            {
                Present = true,
                Pos = Positions.Of(item.transform.position),
                State = item.GetItemState().ToString(),
                Revision = item.AuthorityRevision,
                Leased = NetworkedItemManager.Instance.HasActiveSpatialState(netId),
                Instance = item.gameObject.GetInstanceID(),
                Depth = depth ?? -999f,
                HasTerrain = depth != null
            };
        }

        /// <summary>Host: the server's record of the item, its last move and its Lost and Found state.</summary>
        [Step]
        public static ItemRecord Record(ushort netId)
        {
            var client = UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().FirstOrDefault();
            var result = new ItemRecord
            {
                Lost = LostAndFoundServer.Contains(netId),
                Exists = NetworkedItem.TryGet(netId, out NetworkedItem n) && n != null,
                ClientId = client != null ? client.PlayerId : 0,
                HostId = Multiplayer.Server.Session.NetworkServer.Instance.LocalPlayer?.PlayerId ?? 0
            };
            if (!LiveItems.TryGet(netId, out var r)) return result;
            result.Known = true;
            result.Placement = r.Placement.ToString();
            result.Owner = OwnerNumbers.Of(r.Owner);
            result.Handle = ItemHandles.Of(r.PersistentItemId);
            result.PlacementPlayer = PlayerNumbers.Of(r.Holder);
            result.Revision = r.Revision;
            result.ParentKind = r.WorldParentKind.ToString();
            result.ParentNetId = r.WorldParentNetId;
            // The record keeps absolute positions.
            result.Pos = new Abs { X = (float)Math.Round(r.Position.x, 2), Y = (float)Math.Round(r.Position.y, 2), Z = (float)Math.Round(r.Position.z, 2) };
            result.LastMove = r.LastMove.Reason.ToString();
            result.LastMoveDetail = r.LastMove.Detail ?? "";
            return result;
        }

        /// <summary>Where this game has the item relative to the car: attached to it, and where in its frame.</summary>
        [Step]
        public static ItemOnCar OnCar(ushort netId, string car)
        {
            TrainCar c = Trains.Find(car);
            if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null) return new ItemOnCar();
            Vector3 local = c.transform.InverseTransformPoint(item.transform.position);
            return new ItemOnCar
            {
                Present = true,
                InCar = TrainCar.Resolve(item.transform) == c,
                Local = new Point { X = local.x, Y = local.y, Z = local.z },
                State = item.GetItemState().ToString(),
                CarNetId = Multiplayer.Shared.Utils.DvExtensions.GetNetId(c)
            };
        }

        /// <summary>This game's local player number (NetworkLifecycle.LocalSession; 0 for none).</summary>
        [Step]
        public static int LocalPlayerId() => NetworkLifecycle.Instance.LocalSession?.PlayerId ?? 0;

        /// <summary>Records an item a scenario's own step spawned, so the framework removes it
        /// after the run and the leak check counts it.</summary>
        public static void Track(ushort netId) => spawned.Add(netId);
    }

    public static class Walk
    {
        /// <summary>Moves this game's player <paramref name="dx"/>, <paramref name="dz"/> metres from
        /// where they stand, onto the ground there. Result: where they are then.</summary>
        [Step]
        public static IEnumerator Away(Step<Abs> step, float dx, float dz)
        {
            Vector3 now = Dev.Abs(PlayerManager.PlayerTransform.position);
            yield return LocalPlayer.TeleportToGround(now.x + dx, now.z + dz);
            step.Result = Positions.Of(PlayerManager.PlayerTransform.position);
        }
    }

    public static class Saves
    {
        /// <summary>Host: a manual save, given 3 s to be written. Real seconds: a dedicated host with
        /// nobody online is paused, and a scaled wait never ends there.</summary>
        [Step]
        public static IEnumerator Manual()
        {
            SaveGameManager.Instance.Save(DV.Common.SaveType.Manual);
            yield return new WaitForSecondsRealtime(3);
        }
    }

    public static class ServerRequests
    {
        /// <summary>Host: which of these requests (packet type names, e.g.
        /// ServerboundItemRequestPacket) the server does NOT handle in-process for the host's own
        /// player, going over its loopback client instead (docs/design/host-local-session.md,
        /// stage 2). None is what's wanted.</summary>
        [Step]
        public static string[] NotHandledLocally(string[] types)
        {
            var server = Multiplayer.Server.Session.NetworkServer.Instance;
            Type[] packets = server.GetType().Assembly.GetTypes()
                .Where(t => t.Namespace?.StartsWith("Multiplayer.Shared.Protocol.Packets", StringComparison.Ordinal) == true).ToArray();
            return types.Where(name => !server.localRequests.Handles(
                packets.SingleOrDefault(t => t.Name == name) ?? throw new ArgumentException("no packet " + name))).ToArray();
        }
    }

    public static partial class Cars
    {
        /// <summary>
        /// Host: a car of <paramref name="livery"/> (an ID, or the first livery whose ID starts with
        /// it: "Flatbed") on the track nearest a point around a player, <paramref name="near"/>
        /// "host" or "client" (on a dedicated host, the host's player transform follows the client), as
        /// Trains.SpawnNearPlayer does around the host's, its handbrake on. Result: its ID, 120 frames later.
        /// </summary>
        [Step]
        public static IEnumerator SpawnCarNear(Step<string> step, string livery, string near)
        {
            var l = DV.Globals.G.Types.Liveries.FirstOrDefault(x => x.id == livery)
                    ?? DV.Globals.G.Types.Liveries.FirstOrDefault(x => x.id.StartsWith(livery))
                    ?? throw new ArgumentException("no livery " + livery);
            Transform player = near == "host"
                ? PlayerManager.PlayerTransform
                : UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().transform;
            Vector3 forward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            TrainCar car = null;
            foreach (float distance in new[] { 25f, 40f, 60f })
            foreach (Vector3 dir in new[] { forward, right, -right, -forward })
                if (car == null)
                    car = CarSpawner.Instance.SpawnCarOnClosestTrack(player.position + dir * distance, l, false, true, false);
            if (car == null) throw new Exception($"no room to spawn {l.id} near the {near}");
            // Its handbrake on, so it stays beside the player: one spawned on a grade rolled 150 m
            // away in 20 s (attached-offline). Cars.Push releases it to move the car.
            if (car.brakeSystem != null && car.brakeSystem.hasHandbrake) car.brakeSystem.SetHandbrakePosition(1f);
            spawned.Add(car.ID);
            for (int i = 0; i < 120; i++) yield return null;
            step.Result = car.ID;
        }
    }
}
