using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using DV.Items;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// An item's own saved state through the inventory (docs/design/items/inventory.md, "State";
    /// D14, OI-COLD-13): a client changes a lighter (lid open) and duct tape (two uses gone) in
    /// the hand, loads each vanilla magazine (the sprayer, the soldering tool) with its ammo (a
    /// paint can, a spool), and stashes each one. The stand-in keeps the state the object had; a
    /// take-out makes the object with it in its first frame (a magazine with its ammo inside);
    /// after a rejoin the row brings it down again and a second take-out has it too. A remote
    /// client only: the host's own player keeps objects until R4C C4.
    /// </summary>
    [Scenario("stash-state", "A lighter, duct tape and the magazines with their ammo keep their saved state through stash, take-out and a rejoin")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class StashState : Scenario<StashState.Options>
    {
        public sealed class Options
        {
            /// <summary>The items changed in the hand, comma-separated.</summary>
            public string Prefabs = "lighter,DuctTape";
            /// <summary>Also every vanilla magazine, loaded with its ammo.</summary>
            public bool Magazines = true;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            var items = o.Prefabs.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => (prefab: p, ammo: "")).ToList();
            if (o.Magazines)
                foreach (string pairing in await pair.Client.Run(() => InGame.Magazines()))
                    items.Add((pairing.Split('/')[0], pairing.Split('/')[1]));
            Report.Note("items: " + string.Join(", ", items.Select(i => i.ammo.Length > 0 ? $"{i.prefab} with {i.ammo}" : i.prefab)));

            var ids = new List<string>();
            var before = new List<Kept>();
            foreach (var (prefab, ammo) in items)
            {
                ushort netId = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, "client"));
                ushort ammoId = ammo.Length > 0 ? await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, ammo, "client")) : (ushort)0;
                Kept kept = await pair.Client.Run<Kept>(s => InGame.ChangeAndStash(s, netId, ammoId), 90);
                Report.Note($"{prefab} {netId}, stashed: {kept}");
                Expect.That(kept.StandIn, $"{prefab}: a stand-in in the client's slot");
                Expect.Equal(kept.State, kept.StandInState, $"{prefab}: the stand-in's state, against the object's at put-away");
                Kept out1 = await pair.Client.Run<Kept>(s => InGame.TakeOutAndStash(s, netId), 60);
                Report.Note($"{prefab}, taken out: {out1}");
                Expect.Equal(kept.State, out1.State, $"{prefab}: its state in the take-out's first frame");
                Expect.Equal(kept.Contents, out1.Contents, $"{prefab}: what's in it in the take-out's first frame");
                Expect.That(out1.StandIn, $"{prefab}: a stand-in again after the second stash");
                ids.Add(await pair.Host.Run(() => Owned.IdOf(netId)));
                before.Add(out1);
            }

            await pair.Client.Quit();
            await Task.Delay(4000);
            await pair.Rejoin();
            await Task.Delay(12000);   // the client receives its rows

            for (int i = 0; i < items.Count; i++)
            {
                string id = ids[i], prefab = items[i].prefab;
                ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                await pair.Host.Run(() => Ledger.Track(netId));
                Expect.That(netId != 0, $"{prefab}: the server still has it after the rejoin");
                if (netId == 0) continue;
                string row = await pair.Client.Run(() => InGame.StandInState(netId));
                Expect.Equal(before[i].StandInState, row, $"{prefab}: its row's state after the rejoin");
                Kept again = await pair.Client.Run<Kept>(s => InGame.TakeOutAndStash(s, netId), 60);
                Report.Note($"{prefab}, taken out after the rejoin: {again}");
                Expect.Equal(before[i].State, again.State, $"{prefab}: its state after the rejoin, in the take-out's first frame");
                Expect.Equal(before[i].Contents, again.Contents, $"{prefab}: what's in it after the rejoin");
            }
        }

        public sealed class Kept
        {
            public string State = "", StandInState = "", Contents = "";
            public bool StandIn;
            public int Slot = -1;

            public override string ToString() =>
                $"slot {Slot}{(StandIn ? " (stand-in)" : "")}, state {State}" + (Contents.Length > 0 ? $", holds {Contents}" : "");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Each vanilla magazine with an ammo prefab that fits it, "Magazine/Ammo".</summary>
            [Step]
            public static string[] Magazines()
            {
                var specs = DV.Globals.G.Items.items.Where(s => s != null).ToList();
                var ammo = specs.Where(s => s.GetComponentInChildren<MagazineAmmo>(true) != null).ToList();
                return specs.Select(s => (spec: s, magazine: s.GetComponentInChildren<ItemMagazine>(true)))
                    .Where(m => m.magazine != null)
                    .Select(m => (m.spec.ItemPrefabName, fit: ammo.FirstOrDefault(a => m.magazine.ValidItem(a.GetComponentInChildren<MagazineAmmo>(true), true))))
                    .Where(m => m.fit != null)
                    .Select(m => m.ItemPrefabName + "/" + m.fit.ItemPrefabName).Distinct().ToArray();
            }

            /// <summary>
            /// Client: picks the item up, changes its state (a lighter's lid, two uses of duct tape)
            /// or loads its ammo into it, and stashes it. Result: the state it went in with, and its
            /// stand-in's.
            /// </summary>
            [Step]
            public static IEnumerator ChangeAndStash(Step<Kept> step, ushort id, ushort ammoId)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                if (ammoId != 0)
                {
                    // The ammo first, into the magazine where it lies (as a player reloads it).
                    yield return Items.Arrive(ammoId);
                    yield return Items.Arrive(id);
                    NetworkedItem.TryGet(id, out NetworkedItem tool);
                    NetworkedItem.TryGet(ammoId, out NetworkedItem ammo);
                    yield return LocalPlayer.GoTo(ammo);
                    yield return Hands.Pickup(ammoId);
                    var magazine = tool.GetComponentInChildren<ItemMagazine>(true);
                    if (!magazine.AddItem(ammo.gameObject, 0)) throw new Exception($"the magazine won't take ammo {ammoId}");
                    for (int i = 0; i < 60; i++) yield return null;
                }
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                item.GetComponentInChildren<Lighter>(true)?.OpenLid(false);
                var tape = item.GetComponentInChildren<DV.Customization.Gadgets.Implementations.DuctTape>(true);
                if (tape != null) { tape.ConsumeOneUse(); tape.ConsumeOneUse(); }
                for (int i = 0; i < 10; i++) yield return null;
                var kept = new Kept { State = Normal(ItemStateText.Capture(item.gameObject)), Contents = Contents(item.gameObject) };
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                yield return new WaitForSecondsRealtime(1.5f);
                kept.Slot = PlayerInventory.Slot(id);
                kept.StandIn = PlayerInventory.IsStandIn(id);
                kept.StandInState = StandInState(id);
                Hands.ReleaseControl();
                step.Result = kept;
            }

            /// <summary>Client: the stored item's state as its stand-in has it, "" for none.</summary>
            [Step]
            public static string StandInState(ushort id) =>
                InventoryStandIn.TryGet(id, out InventoryStandIn standIn) ? Normal(standIn.State) : "";

            /// <summary>
            /// Client: takes the stored item out to the hand, reads its state and contents in that
            /// frame, and stashes it again. Result: what it read, and its stand-in after.
            /// </summary>
            [Step]
            public static IEnumerator TakeOutAndStash(Step<Kept> step, ushort id)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                var inv = DV.InventorySystem.Inventory.Instance;
                GameObject stored = PlayerInventory.SlotObject(id) ?? throw new Exception($"item {id} isn't in the inventory");
                inv.EquipItem(stored, PlayerInventory.Slot(id), 0);
                if (!NetworkedItem.TryGet(id, out NetworkedItem item) || item == null) throw new Exception("taken out, no object in the same frame");
                var kept = new Kept { State = Normal(ItemStateText.Capture(item.gameObject)), Contents = Contents(item.gameObject) };
                yield return Wait.Until(() => Hands.Held != null && Hands.Held.NetId == id, 5f, "the item in the hand");
                yield return new WaitForSecondsRealtime(1f);
                inv.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                yield return new WaitForSecondsRealtime(1.5f);
                kept.Slot = PlayerInventory.Slot(id);
                kept.StandIn = PlayerInventory.IsStandIn(id);
                kept.StandInState = StandInState(id);
                Hands.ReleaseControl();
                step.Result = kept;
            }

            // A magazine's ammo, by prefab and state; "" for none or no magazine.
            private static string Contents(GameObject go)
            {
                var magazine = go.GetComponentInChildren<ItemMagazine>(true);
                if (magazine == null) return "";
                return string.Join(", ", Enumerable.Range(0, magazine.Capacity).Select(i => magazine[i]).Where(a => a != null)
                    .Select(a => a.name.Replace("(Clone)", "") + " " + Normal(ItemStateText.Capture(a))));
            }

            // The state as compact JSON with its keys sorted, so two reads compare.
            private static string Normal(string state)
            {
                if (string.IsNullOrEmpty(state)) return "";
                return Sorted(JToken.Parse(state)).ToString(Newtonsoft.Json.Formatting.None);
            }

            private static JToken Sorted(JToken token) => token is JObject o
                ? new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sorted(p.Value))))
                : token;
        }
    }
}
