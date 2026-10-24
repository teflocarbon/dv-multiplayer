using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using DV.Items;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A loaded tool's ammo wherever the tool goes (docs/design/items/containers.md, "Ammo in a
    /// magazine"; the R5 stack review's E1, E2 and E6). The client loads its ammo into its tool:
    /// the server has it in the magazine (E1). The tool goes into a box and out (MagazineServer's
    /// Stow and LoadFromRows), to Lost and Found and back, and is destroyed (Release): the ammo
    /// goes with it as a row of the tool and is loaded again on both games as it comes back, and
    /// comes out loose when the tool is gone. The client then loads its ammo into a tool that isn't
    /// its own (the host player's, stashed by the host, on a player host; nobody's lying in the
    /// world on a dedicated one) and retrieves it from its owned-items list: it's in its Lost and
    /// Found, and no copy of the tool still holds it (E2). Last, a stashed loaded tool's take-out is
    /// refused, with and without a correction: the stand-in is back with the ammo nested in its row,
    /// and the server still has the ammo in the magazine; unrefused, the tool comes out loaded.
    /// </summary>
    [Scenario("magazine-tools", "A loaded tool's ammo goes with it into a box and out, to Lost and Found and back, and comes out when it's destroyed; loaded ammo retrieved from someone else's tool leaves no copy; a refused take-out keeps the ammo nested")]
    [Budget(900)]
    public sealed class MagazineTools : Scenario<MagazineTools.Options>
    {
        public sealed class Options
        {
            /// <summary>The tool: one with a magazine.</summary>
            public string Tool = "Boombox";
            /// <summary>The box the tool goes into.</summary>
            public string Box = "ItemContainerToolbox";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            string ammoPrefab = (await pair.Host.Run(() => StashState.InGame.Magazines()))
                .Select(m => m.Split('/')).Where(m => m[0] == o.Tool).Select(m => m[1]).FirstOrDefault()
                ?? throw new HarnessException($"no ammo fits {o.Tool}");
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            var made = new List<string>();
            pair.Cleanup(async () =>
            {
                await pair.Host.Run(() => TakeoutRefused.InGame.Refuse(0, false));
                ushort[] left = await pair.Host.Run(() => InGame.NetIdsOf(made.ToArray()));
                await pair.Host.Run<ushort[]>(s => Items.Destroy(s, left), 120);
            });
            async Task<(ushort net, string id)> Spawn(string prefab, string near, bool personal = true)
            {
                ushort net = personal
                    ? await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, prefab, near))
                    : await pair.Host.Run<ushort>(s => Items.Spawn(s, prefab, near));
                string id = await pair.Host.Run(() => Owned.IdOf(net));
                made.Add(id);
                return (net, id);
            }
            async Task ExpectLoaded(ushort toolNet, string loaded, string what)
            {
                await Task.Delay(2000);
                Expect.Equal(loaded, await pair.Client.Run(() => ItemBugMagazineAmmo.InGame.HoldsOf(toolNet)), $"{what}: what the client's copy of the tool holds");
                Expect.Equal(loaded, await pair.Host.Run(() => ItemBugMagazineAmmo.InGame.HoldsOf(toolNet)), $"{what}: what the host's copy of the tool holds");
            }

            // The client loads its ammo into its tool.
            (ushort tool, string toolId) = await Spawn(o.Tool, "client");
            (ushort ammo, string ammoId) = await Spawn(ammoPrefab, "client");
            string loaded = await pair.Client.Run<string>(s => InGame.Load(s, tool, ammo), 60);
            Report.Note($"{o.Tool} {tool} ({toolId}) loaded with {ammoPrefab} {ammo} ({ammoId}): [{loaded}]");
            Require.That(loaded.Length > 0, "the client loaded the ammo into the tool");
            await ExpectLoaded(tool, loaded, "loaded by the client");
            Expect.Equal("Magazine", (await pair.Host.Run(() => Owned.Entry(ammoId))).Place, "the ammo's ledger home once the client loaded it (E1)");

            // Into a box and out.
            (ushort box, string boxId) = await Spawn(o.Box, "client");
            Require.Equal(Boxes.Accepted, await pair.Client.Run<string>(s => Boxes.Deposit(s, box, tool), 120), "the loaded tool into the box");
            Expect.Equal(boxId, await pair.Host.Run(() => Boxes.StoredIn(toolId)), "the box the tool is in");
            Expect.Equal(toolId, await pair.Host.Run(() => Boxes.StoredIn(ammoId)), "the ammo is a row of the tool, in the box (Stow)");
            Expect.Equal("Container", (await pair.Host.Run(() => Owned.Entry(ammoId))).Place, "the ammo's ledger home, in the box with its tool");
            string withdrawn = await pair.Client.Run<string>(s => Boxes.Withdraw(s, box, -1), 120);
            Require.That(withdrawn.StartsWith(Boxes.Accepted), "the tool out of the box", withdrawn);
            tool = ushort.Parse(withdrawn.Substring(Boxes.Accepted.Length + 1));
            await pair.Client.Run(() => InGame.ToGround(tool), 60);
            await ExpectLoaded(tool, loaded, "out of the box (LoadFromRows)");
            Expect.Equal("Magazine", (await pair.Host.Run(() => Owned.Entry(ammoId))).Place, "the ammo's ledger home, out of the box");

            // To Lost and Found and back.
            Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, tool, "ContainerCleared"), 30), "the loaded tool collected to the client's Lost and Found");
            Expect.Equal(toolId, await pair.Host.Run(() => Boxes.StoredIn(ammoId)), "the ammo is a row of the tool in Lost and Found");
            uint handle = await pair.Host.Run(() => Owned.HandleOf(tool));
            await pair.Client.Run<int>(s => LostAndFound.InGame.Retrieve(s, handle), 60);
            tool = await pair.Host.Run(() => Owned.NetIdOf(toolId));
            await pair.Client.Run(() => InGame.ToGround(tool), 60);
            await ExpectLoaded(tool, loaded, "back from Lost and Found");
            Expect.Equal("Magazine", (await pair.Host.Run(() => Owned.Entry(ammoId))).Place, "the ammo's ledger home, back from Lost and Found");

            // The tool destroyed: the ammo comes out (Release).
            await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { tool }), 60);
            await Task.Delay(2000);
            ammo = await pair.Host.Run(() => Owned.NetIdOf(ammoId));
            Expect.That(ammo != 0, "the ammo is still there once its tool is destroyed");
            LedgerEntry released = await pair.Host.Run(() => Owned.Entry(ammoId));
            Report.Note($"the ammo after its tool went: {released.Where}");
            Expect.That(released.Live && released.Place != "Magazine" && released.Place != "Container", "the ammo is loose once its tool is destroyed", released.Where);
            if (ammo != 0)
            {
                Expect.That(!await pair.Host.Run(() => InGame.InMagazine(ammo)), "the host's ammo isn't in a magazine");
                Expect.That(!await pair.Client.Run(() => InGame.InMagazine(ammo)), "the client's ammo isn't in a magazine");
            }

            // Loaded into a tool that isn't the client's, and retrieved (E2).
            if (ammo == 0) return;
            (ushort other, string otherId) = pair.Dedicated ? await Spawn(o.Tool, "client", personal: false) : await Spawn(o.Tool, "host");
            string inOther = await pair.Client.Run<string>(s => InGame.Load(s, other, ammo), 60);
            Require.That(inOther.Length > 0, "the client loaded its ammo into the other tool");
            Expect.Equal("Magazine", (await pair.Host.Run(() => Owned.Entry(ammoId))).Place, "the ammo's ledger home in the other tool");
            if (!pair.Dedicated)
            {
                await pair.Host.Run<int>(s => Items.Stash(s, other, false), 60);
                Report.Note("the host's player stashed its tool with the client's ammo in it");
            }
            OwnedRetrieve.Result r = await Retrieve(pair, client, ammoId);
            Expect.That(r.Accepted, "the client's retrieval of its ammo from the other tool is accepted", r.Reason);
            await Task.Delay(2000);
            OwnerInfo ammoNow = await pair.Host.Run(() => Ownership.ById(ammoId));
            Expect.That(ammoNow.Lost && ammoNow.Owner == client, "the ammo is in the client's Lost and Found", ammoNow.ToString());
            Expect.Equal(0, await pair.Host.Run(() => InGame.AmmoCount(other)), "the ammo the server has in the other tool after the retrieval");
            Expect.Equal("", await pair.Client.Run(() => ItemBugMagazineAmmo.InGame.HoldsOf(other)), "what the client's copy of the other tool holds after the retrieval");
            if (!pair.Dedicated)
                Expect.Equal("", await pair.Host.Run<string>(s => InGame.TakeOutAndRead(s, other), 60), "what the host's tool holds, taken out after the retrieval (no copy left)");
            else
                Expect.Equal("", await pair.Host.Run(() => ItemBugMagazineAmmo.InGame.HoldsOf(other)), "what the host's copy of the other tool holds after the retrieval");

            // A stashed loaded tool's take-out refused: the ammo stays nested.
            (ushort stashed, string stashedId) = await Spawn(o.Tool, "client");
            (ushort spare, string spareId) = await Spawn(ammoPrefab, "client");
            string inStashed = await pair.Client.Run<string>(s => InGame.Load(s, stashed, spare), 60);
            Require.That(inStashed.Length > 0, "the client loaded the tool it stashes");
            await pair.Client.Run<int>(s => Items.Stash(s, stashed, false), 60);
            Require.That(await pair.Client.Run(() => InGame.Nests(stashed)), "the stashed tool's stand-in has the ammo nested in its row");
            foreach (bool correct in new[] { false, true })
            {
                string what = $"a take-out refused {(correct ? "with" : "without")} a correction";
                await pair.Host.Run(() => TakeoutRefused.InGame.Refuse(stashed, correct));
                TakeoutRefused.Out took = await pair.Client.Run<TakeoutRefused.Out>(s => TakeoutRefused.InGame.TakeOut(s, stashed, "hand"), 60);
                Report.Note($"{what}: {took}");
                Expect.Equal(1, await pair.Host.Run(() => TakeoutRefused.InGame.Refused()), $"{what}: the requests the host refused");
                Expect.That(took.BackAsStandIn && !took.ObjectAfter, $"{what}: the stand-in is back and no object is left", took.ToString());
                Expect.That(await pair.Client.Run(() => InGame.Nests(stashed)), $"{what}: the stand-in still has the ammo nested");
                Expect.That(!await pair.Client.Run(() => InGame.Loose(spare)), $"{what}: no loose object of the ammo on the client");
                Expect.Equal("Magazine", (await pair.Host.Run(() => Owned.Entry(spareId))).Place, $"{what}: the ammo's ledger home");
                Expect.Equal(1, await pair.Host.Run(() => InGame.AmmoCount(stashed)), $"{what}: the ammo the server has in the tool");
            }
            await pair.Host.Run(() => TakeoutRefused.InGame.Refuse(0, false));
            TakeoutRefused.Out ok = await pair.Client.Run<TakeoutRefused.Out>(s => TakeoutRefused.InGame.TakeOut(s, stashed, "hand"), 60);
            Expect.That(ok.Held, "the take-out not refused: the tool in the hand", ok.ToString());
            await ExpectLoaded(stashed, inStashed, "taken out, not refused");
            await pair.Client.Run(() => Items.Drop());

            Expect.Empty(await pair.Host.Run<string[]>(s => Owned.AuditFor(s, made.ToArray())), "the ledger's audit of this run's items");
        }

        // The client asks for its owned-items list, and retrieves the item by its row's handle.
        private static async Task<OwnedRetrieve.Result> Retrieve(Pair pair, string client, string itemId)
        {
            uint handle = await pair.Host.Run(() => OwnedRetrieve.InGame.HandleOf(client, itemId));
            if (handle == 0) return new OwnedRetrieve.Result { Reason = "not in the server's list" };
            return await pair.Client.Run<OwnedRetrieve.Result>(s => OwnedRetrieve.InGame.Retrieve(s, handle), 30);
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Loads the ammo into the tool where it lies, as a player reloads it, emptying the slot
            /// first if the tool came loaded. Result: what its magazine holds then.
            /// </summary>
            [Step]
            public static IEnumerator Load(Step<string> step, ushort toolId, ushort ammoId)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return Items.Arrive(ammoId);
                yield return Items.Arrive(toolId);
                NetworkedItem.TryGet(toolId, out NetworkedItem tool);
                NetworkedItem.TryGet(ammoId, out NetworkedItem ammo);
                ItemMagazine magazine = tool.GetComponentInChildren<ItemMagazine>(true);
                yield return LocalPlayer.GoTo(ammo);
                yield return Hands.Pickup(ammoId);
                // Let go of it first: the magazine's AddItem doesn't end the grab.
                ammo.Item.ForceEndInteraction();
                yield return Wait.Until(() => Hands.Held == null, 5f, "the ammo out of the hand");
                if (magazine[0] != null)
                    throw new Exception($"tool {toolId} came loaded ({magazine[0].name})");
                if (!magazine.AddItem(ammo.gameObject, 0))
                    throw new Exception($"the magazine won't take ammo {ammoId} (valid {magazine.ValidItem(ammo.gameObject)})");
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = ItemBugMagazineAmmo.InGame.HoldsOf(toolId);
            }

            /// <summary>Puts the item on the ground at the player's feet, out of the inventory or the hand.</summary>
            [Step]
            public static IEnumerator ToGround(ushort netId)
            {
                if (PlayerInventory.Holds(netId))
                {
                    if (Hands.Held != null) yield return Hands.Drop();
                    yield return PlayerInventory.TakeOut(netId);
                }
                if (Hands.Held != null && Hands.Held.NetId == netId)
                    yield return Hands.Drop();
                for (int i = 0; i < 60; i++) yield return null;
            }

            /// <summary>Whether this game's object of the item is in a magazine.</summary>
            [Step]
            public static bool InMagazine(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null && item.Item?.InContainer is ItemMagazine;

            /// <summary>Whether this game has a loose object of the item (not in a magazine).</summary>
            [Step]
            public static bool Loose(ushort netId) =>
                NetworkedItem.TryGet(netId, out NetworkedItem item) && item != null && item.Item?.InContainer is not ItemMagazine;

            /// <summary>Whether the item's stand-in has ammo nested in its row.</summary>
            [Step]
            public static bool Nests(ushort netId) =>
                InventoryStandIn.TryGet(netId, out InventoryStandIn standIn) && (standIn.State ?? "").Contains(StandInAmmo.Key);

            /// <summary>Host: the ammo the server has in the tool's magazine, as objects or as data.</summary>
            [Step]
            public static int AmmoCount(ushort toolNetId) => LiveItems.AmmoIn(toolNetId).Count();

            /// <summary>Host: takes the item out of the host's own inventory to the hand and reads what its magazine holds.</summary>
            [Step]
            public static IEnumerator TakeOutAndRead(Step<string> step, ushort netId)
            {
                if (Hands.Held != null) yield return Hands.Drop();
                yield return PlayerInventory.TakeOut(netId);
                for (int i = 0; i < 60; i++) yield return null;
                step.Result = ItemBugMagazineAmmo.InGame.HoldsOf(netId);
                yield return Hands.Drop();
            }

            /// <summary>Host: the net IDs the items with these ids have now (those still there).</summary>
            [Step]
            public static ushort[] NetIdsOf(string[] ids) =>
                ids.Select(Owned.NetIdOf).Where(net => net != 0).Distinct().ToArray();
        }
    }
}
