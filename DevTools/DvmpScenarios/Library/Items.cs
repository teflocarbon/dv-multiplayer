// Item steps: spawn test items, carry them through DV's real grabber (the probe library's Hands),
// and clean them up. Each [Step] runs inside a game: call it through pair.Host / pair.Client.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>A direction or offset in the world's axes (no floating origin involved).</summary>
    public sealed class Dir
    {
        public float X, Y, Z;
        public static Dir Of(float x, float y, float z) => new() { X = x, Y = y, Z = z };
    }

    public static partial class Items
    {
        // Items the spawn steps made in this game since the last TakeSpawned: removed after a run
        // (Runner: the framework's cleanup), and the leak check's list.
        private static readonly HashSet<ushort> spawned = new();

        /// <summary>
        /// Host: a fresh item of <paramref name="prefab"/> (an item spec's prefab name: "Mug")
        /// beside a player, <paramref name="near"/> "host" or "client", so a run never depends on
        /// loose items nearby. Result: its net ID, once it has one.
        /// </summary>
        [Step]
        public static IEnumerator Spawn(Step<ushort> step, string prefab, string near)
        {
            Vector3 at = near == "host"
                ? PlayerManager.PlayerTransform.position
                : UnityEngine.Object.FindObjectsOfType<Multiplayer.Shared.Players.NetworkedPlayer>().First().transform.position;
            var spec = DV.Globals.G.Items.items.FirstOrDefault(i => i.ItemPrefabName == prefab)
                ?? throw new ArgumentException("no item spec with prefab " + prefab);
            var go = UnityEngine.Object.Instantiate(spec.gameObject, at + Vector3.up * 0.6f + Vector3.right * 0.8f, Quaternion.identity, WorldMover.OriginShiftParent);
            NetworkedItem item = go.GetComponent<NetworkedItem>();
            yield return Wait.Until(() => item != null && item.NetId != 0, 5f, $"the {prefab} to get a net ID");
            spawned.Add(item.NetId);
            for (int i = 0; i < 50; i++) yield return null;
            step.Result = item.NetId;
        }

        /// <summary>Walks to the item and picks it up through the grabber (aims, raycasts, falls back
        /// to a forced hold).</summary>
        [Step]
        public static IEnumerator Pickup(ushort netId)
        {
            NetworkedItem item = Find(netId);
            yield return LocalPlayer.GoTo(item);
            yield return Hands.Pickup(netId);
        }

        [Step]
        public static IEnumerator Drop() => Hands.Drop();

        /// <summary>Throws the held item, along <paramref name="direction"/> or where the player looks.</summary>
        [Step]
        public static IEnumerator Throw(Dir direction) =>
            Hands.Throw(direction == null ? null : new Vector3(direction.X, direction.Y, direction.Z));

        /// <summary>The held item's net ID, 0 for none.</summary>
        [Step]
        public static ushort Held() => Hands.Held != null ? Hands.Held.NetId : (ushort)0;

        /// <summary>Gives the game's input back after Hands took it.</summary>
        [Step]
        public static void ReleaseControl() => Hands.ReleaseControl();

        [Step]
        public static Abs Position(ushort netId) => Positions.Of(Find(netId).transform.position);

        /// <summary>Which of these items this game has: as an object, as a stand-in in an inventory (D14),
        /// or on the host as data only.</summary>
        [Step]
        public static ushort[] Existing(ushort[] ids) =>
            Dev.All<NetworkedItem>().Where(i => i != null && ids.Contains(i.NetId)).Select(i => i.NetId)
                .Concat(Multiplayer.Shared.Items.Inventory.InventoryStandIn.All.Where(s => ids.Contains(s.NetId)).Select(s => s.NetId))
                .Concat(ids.Where(KeptAsData))
                .Distinct().ToArray();

        // The server keeps it as data: in a client's inventory, or in Lost and Found.
        private static bool KeptAsData(ushort id) =>
            PlayerItems.IsDataOnly(id) || Multiplayer.Server.Items.LostAndFound.LostAndFoundServer.IsData(id);

        /// <summary>Host: destroys the items. Result: those still there 30 frames later.</summary>
        [Step]
        public static IEnumerator Destroy(Step<ushort[]> step, ushort[] ids)
        {
            // One the server keeps as data (in a client's inventory, in Lost and Found) is made again
            // first, so its destroy reaches the client. One whose object is going to data this frame
            // (it has just settled in a slot) is data by the next, and is made again like the rest.
            yield return null;
            int making = 0;
            foreach (ushort id in ids.Where(KeptAsData))
            {
                making++;
                PlayerItems.WithObject(id, _ => making--);
            }
            for (int i = 0; i < 300 && making > 0; i++) yield return null;
            // One in Lost and Found is made at once, but goes back to data at the scan after its frame,
            // which may have come while others were still being made: it's made again in this one.
            foreach (ushort id in ids.Where(Multiplayer.Server.Items.LostAndFound.LostAndFoundServer.IsData))
                PlayerItems.WithObject(id, _ => { });
            foreach (NetworkedItem item in Dev.All<NetworkedItem>().Where(i => i != null && ids.Contains(i.NetId) && !i.ColdStorageRetirement).ToList())
                UnityEngine.Object.Destroy(item.gameObject);
            for (int i = 0; i < 30; i++) yield return null;
            step.Result = Existing(ids);
        }

        /// <summary>Host: the net IDs of the items spawn steps made since the last call, and forgets them.</summary>
        [Step]
        public static ushort[] TakeSpawned()
        {
            ushort[] ids = spawned.ToArray();
            spawned.Clear();
            return ids;
        }

        public static NetworkedItem Find(ushort netId) =>
            Dev.All<NetworkedItem>().FirstOrDefault(i => i != null && i.NetId == netId)
            ?? throw new InvalidOperationException("no item with net ID " + netId);
    }

    /// <summary>Waiting inside a game: for a condition, with a deadline and what it waits for.</summary>
    public static class Wait
    {
        /// <summary>Yields until <paramref name="condition"/> holds; throws, naming
        /// <paramref name="what"/>, if it doesn't within <paramref name="seconds"/> (real time).</summary>
        public static IEnumerator Until(Func<bool> condition, float seconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new TimeoutException($"waited {seconds:0.#} s for {what}");
                yield return null;
            }
        }
    }
}
