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
    /// D14, OI-COLD-13): a client changes a lighter (lid open), duct tape (two uses gone) and a
    /// lantern (its wick turned down) in the hand, loads each vanilla magazine (the sprayer, the
    /// soldering tool) with its ammo (a paint can, a spool), and stashes each one. The stand-in
    /// keeps the state the object had (a loaded magazine's ammo nested in its row: OI-CON-16, R5
    /// C8); a take-out makes the object with it, and the ammo in it, in its first frame, and it
    /// still has it after a second in the hand (the server's copy, made afresh, doesn't overwrite
    /// it); after a rejoin the row brings it down again, its ammo nested, and a second take-out has
    /// it too. WHO=host does it
    /// with the host's own player (R4C C4), saving and restarting both games for the rejoin (a
    /// player host only). The items are destroyed at the end.
    /// </summary>
    [Scenario("stash-state", "A lighter, duct tape, a lantern and the magazines with their ammo keep their saved state through stash, take-out and a rejoin")]
    [Needs(Need.RestartsGames)]
    [Budget(600)]
    public sealed class StashState : Scenario<StashState.Options>
    {
        public sealed class Options
        {
            /// <summary>The items changed in the hand, comma-separated.</summary>
            public string Prefabs = "lighter,DuctTape,Lantern";
            /// <summary>Also every vanilla magazine, loaded with its ammo.</summary>
            public bool Magazines = true;
            /// <summary>Whose inventory: "client", or "host" (the host's own player; needs a player host).</summary>
            public string Who = "client";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            bool host = o.Who == "host";
            if (host && pair.Dedicated) throw new HarnessException("WHO=host needs a player host");
            Peer who = host ? pair.Host : pair.Client;
            var items = o.Prefabs.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => (prefab: p, ammo: "")).ToList();
            if (o.Magazines)
                foreach (string pairing in await who.Run(() => InGame.Magazines()))
                    items.Add((pairing.Split('/')[0], pairing.Split('/')[1]));
            Report.Note("items: " + string.Join(", ", items.Select(i => i.ammo.Length > 0 ? $"{i.prefab} with {i.ammo}" : i.prefab)));

            var ids = new List<string>();
            var ammoIds = new List<string>();
            var before = new List<Kept>();
            foreach (var (prefab, ammo) in items)
            {
                ushort netId = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, o.Who));
                ushort ammoId = ammo.Length > 0 ? await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, ammo, o.Who)) : (ushort)0;
                Kept kept = await who.Run<Kept>(s => InGame.ChangeAndStash(s, netId, ammoId), 90);
                Report.Note($"{prefab} {netId}, stashed: {kept}");
                bool loaded = ammo.Length > 0;
                Expect.That(kept.StandIn, $"{prefab}: a stand-in in the {who.Name}'s slot");
                if (loaded)
                    Expect.Equal(kept.Contents, kept.StoredContents, $"{prefab}: its ammo nested in the stand-in (OI-CON-16)");
                Expect.Equal(kept.State, kept.StandInState, $"{prefab}: the stand-in's state, against the object's at put-away");
                Kept out1 = await who.Run<Kept>(s => InGame.TakeOutAndStash(s, netId), 60);
                Report.Note($"{prefab}, taken out: {out1}");
                Expect.Equal(kept.State, out1.State, $"{prefab}: its state in the take-out's first frame");
                Expect.Equal(kept.Contents, out1.Contents, $"{prefab}: what's in it in the take-out's first frame");
                Expect.Equal(kept.State, out1.StandInState, $"{prefab}: its state after a second in the hand, stashed again");
                Expect.That(out1.StandIn, $"{prefab}: a stand-in again after the second stash");
                if (loaded)
                    Expect.Equal(kept.Contents, out1.StoredContents, $"{prefab}: its ammo nested in the stand-in after the second stash");
                ids.Add(await pair.Host.Run(() => Owned.IdOf(netId)));
                if (ammoId != 0) ammoIds.Add(await pair.Host.Run(() => Owned.IdOf(ammoId)));
                before.Add(out1);
            }

            if (host)
            {
                await pair.Host.Run(() => Saves.Manual());
                await pair.Restart();
                await Task.Delay(12000);   // the host's rows are drawn
            }
            else
            {
                await pair.Client.Quit();
                await Task.Delay(4000);
                await pair.Rejoin();
                await Task.Delay(12000);   // the client receives its rows
            }
            var after = new List<ushort>();
            pair.Cleanup(async () => await pair.Host.Run<ushort[]>(s => Items.Destroy(s, after.ToArray()), 30));
            foreach (string ammoId in ammoIds)
            {
                ushort ammoNetId = await pair.Host.Run(() => Owned.NetIdOf(ammoId));
                if (ammoNetId != 0) after.Add(ammoNetId);
            }

            for (int i = 0; i < items.Count; i++)
            {
                string id = ids[i], prefab = items[i].prefab;
                ushort netId = await pair.Host.Run(() => Owned.NetIdOf(id));
                await pair.Host.Run(() => Ledger.Track(netId));
                Expect.That(netId != 0, $"{prefab}: the server still has it after the rejoin");
                if (netId == 0) continue;
                after.Add(netId);
                string row = await who.Run(() => InGame.StandInState(netId));
                Expect.Equal(before[i].StandInState, row, $"{prefab}: its row's state after the rejoin");
                if (items[i].ammo.Length > 0)
                {
                    // Its ammo went cold with it as rows of its container, and came back nested in its row (OI-CON-16).
                    string holds = await who.Run(() => InGame.StoredContents(netId));
                    Expect.Equal(before[i].Contents, holds, $"{prefab}: its ammo nested in the stand-in after the rejoin");
                }
                Kept again = await who.Run<Kept>(s => InGame.TakeOutAndStash(s, netId), 60);
                Report.Note($"{prefab}, taken out after the rejoin: {again}");
                Expect.Equal(before[i].State, again.State, $"{prefab}: its state after the rejoin, in the take-out's first frame");
                Expect.Equal(before[i].Contents, again.Contents, $"{prefab}: what's in it after the rejoin");
                Expect.Equal(before[i].State, again.StandInState, $"{prefab}: its state after the rejoin and a second in the hand, stashed again");
            }
        }

        public sealed class Kept
        {
            public string State = "", StandInState = "", Contents = "", StoredContents = "";
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
            /// Client: picks the item up, changes its state (a lighter's lid, two uses of duct tape,
            /// a lantern's wick) or loads its ammo into it, and stashes it. Result: the state it went in with, and its
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
                    // Let go of it first: the magazine's AddItem doesn't end the grab.
                    ammo.Item.ForceEndInteraction();
                    yield return Wait.Until(() => Hands.Held == null, 5f, "the ammo out of the hand");
                    // One that came loaded keeps its own ammo (the spare is the run's, destroyed at its end).
                    if (magazine[0] == null && !magazine.AddItem(ammo.gameObject, 0))
                        throw new Exception($"the magazine won't take ammo {ammoId} (valid {magazine.ValidItem(ammo.gameObject)})");
                    for (int i = 0; i < 60; i++) yield return null;
                }
                yield return Items.Arrive(id);
                NetworkedItem.TryGet(id, out NetworkedItem item);
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(id);
                item.GetComponentInChildren<Lighter>(true)?.OpenLid(false);
                var tape = item.GetComponentInChildren<DV.Customization.Gadgets.Implementations.DuctTape>(true);
                if (tape != null) { tape.ConsumeOneUse(); tape.ConsumeOneUse(); }
                var lantern = item.GetComponentInChildren<Lantern>(true);
                if (lantern != null) lantern.knob.SetValue(0.6f);
                for (int i = 0; i < (lantern != null ? 60 : 10); i++) yield return null;
                var kept = new Kept { State = Normal(ItemStateText.Capture(item.gameObject)), Contents = Contents(item.gameObject) };
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(item.gameObject);
                yield return Wait.Until(() => PlayerInventory.Holds(id), 5f, "the stash");
                yield return new WaitForSecondsRealtime(1.5f);
                kept.Slot = PlayerInventory.Slot(id);
                kept.StandIn = PlayerInventory.IsStandIn(id);
                kept.StandInState = StandInState(id);
                kept.StoredContents = StoredContents(id);
                Hands.ReleaseControl();
                step.Result = kept;
            }

            /// <summary>Client: the stored item's state as its stand-in has it (or its object in the slot), "" for none.</summary>
            [Step]
            public static string StandInState(ushort id) =>
                InventoryStandIn.TryGet(id, out InventoryStandIn standIn) ? Normal(StandInAmmo.Without(standIn.State)) :
                PlayerInventory.Holds(id) ? Normal(ItemStateText.Capture(PlayerInventory.SlotObject(id))) : "";

            /// <summary>Client: what a stored item's magazine holds (a stand-in's nested ammo, or its object's), "" for none.</summary>
            [Step]
            public static string StoredContents(ushort id) =>
                InventoryStandIn.TryGet(id, out InventoryStandIn standIn)
                    ? string.Join(", ", StandInAmmo.Of(standIn.State).OrderBy(a => a.Slot)
                        .Select(a => a.PrefabName + " " + Normal(a.State?.ToString(Newtonsoft.Json.Formatting.None))))
                    : PlayerInventory.Holds(id) ? Contents(PlayerInventory.SlotObject(id)) : "";

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
                kept.StoredContents = StoredContents(id);
                Hands.ReleaseControl();
                step.Result = kept;
            }

            // A magazine's ammo, by prefab and state; "" for none or no magazine.
            private static string Contents(GameObject go)
            {
                var magazine = go.GetComponentInChildren<ItemMagazine>(true);
                if (magazine == null) return "";
                return string.Join(", ", Enumerable.Range(0, magazine.Capacity).Select(i => magazine[i]).Where(a => a != null)
                    .Select(a => (a.GetComponent<InventoryItemSpec>()?.ItemPrefabName ?? a.name.Replace("(Clone)", "")) + " " +
                                 Normal(ItemStateText.Capture(a))));
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
