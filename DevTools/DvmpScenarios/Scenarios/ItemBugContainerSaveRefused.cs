using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Shared.Items.LiveState;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CUI = Multiplayer.Client.Items.Containers.ContainerUi;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bug F18 (docs/research/item-bug-review.md), made impossible by R4's rows that stand alone
    /// (docs/design/items/save-load.md, OI-CON-04, OI-PER-23). A cold-container save the server
    /// refused used to leave the graph empty, and the next save wrote that empty graph over it: every
    /// player's stored items were gone. Now each stored item is a row of its own. The run stores a mug
    /// and a flashlight in the client's toolbox, checks containers are refused until the load has run,
    /// and saves with the mug's row made unloadable the way a capacity change leaves it (its slot past
    /// the end). After a restart the flashlight must still be in the toolbox, and the mug in the
    /// client's Lost and Found (<c>SaveRecovery</c>), with its id; the next save must hold both.
    /// </summary>
    [Scenario("item-bug-container-save-refused", "A stored item's saved row the load refuses stands alone: the container's other items load, the refused one goes to its owner's Lost and Found, and containers wait for the load (F18)")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class ItemBugContainerSaveRefused : Scenario<ItemBugContainerSaveRefused.Options>
    {
        public sealed class Options
        {
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>The item whose row is made unloadable.</summary>
            public string Prefab = "Mug";
            /// <summary>The item that stays in the container.</summary>
            public string Other = "Flashlight";
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            ushort other = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Other, "client"));
            string mugId = await pair.Host.Run(() => Owned.IdOf(mug));
            string otherId = await pair.Host.Run(() => Owned.IdOf(other));
            Require.That(mugId.Length > 0 && otherId.Length > 0, "both items have ids", $"{mugId} {otherId}");
            await pair.Client.Run(() => InGame.Deposit(box, mug), 120);
            await pair.Client.Run(() => InGame.Deposit(box, other), 120);
            Require.Equal("Container", (await pair.Host.Run(() => Owned.Entry(mugId))).Place, "the mug's home after the deposit");
            Require.Equal("Container", (await pair.Host.Run(() => Owned.Entry(otherId))).Place, "the flashlight's home after the deposit");

            string browse = await pair.Host.Run(() => InGame.BrowseBeforeTheLoad(box));
            Report.Note("browsing the client's container before the load has run: " + browse);
            Expect.That(browse.Contains("items-loading"), "containers are refused until the load has run (OI-CON-05)", browse);

            string bad = await pair.Host.Run<string>(s => InGame.SaveWithBadRow(s, mugId), 60);
            Report.Note("saved the mug's ledger row as " + bad);
            Require.That(bad.Length > 0, "the mug's saved row was changed", bad);

            await pair.Restart(o.Session);
            await Task.Delay(15000);
            LedgerEntry mugAfter = await pair.Host.Run(() => Owned.Entry(mugId));
            LedgerEntry otherAfter = await pair.Host.Run(() => Owned.Entry(otherId));
            Report.Note($"after the restart: {mugAfter}; {otherAfter}");
            Expect.That(otherAfter.Live && otherAfter.Place == "Container", "the flashlight is still in the toolbox", otherAfter.ToString());
            Expect.That(mugAfter.Live && mugAfter.Place == "LostAndFound", "the refused mug is in its owner's Lost and Found", mugAfter.ToString());
            Expect.Equal("SaveRecovery", await pair.Host.Run(() => InGame.LostReason(mugId)), "why the mug is in Lost and Found");
            Expect.That((await pair.Host.LogsSince(0, "warning")).Any(l => l.Message.Contains(mugId) && l.Message.Contains("its container refused it")),
                "the refusal is logged");

            await pair.Host.Run(() => Saves.Manual());
            string saved = await pair.Host.Run(() => InGame.SavedHomes(mugId, otherId));
            Report.Note("saved again: " + saved);
            Expect.Equal("LostAndFound,Container", saved, "the next save's homes for the mug and the flashlight");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: stashes the item, takes the container and deposits the item in it.</summary>
            [Step]
            public static IEnumerator Deposit(ushort boxId, ushort itemId)
            {
                var inv = DV.InventorySystem.Inventory.Instance;
                NetworkedItem box = null, item = null;
                yield return Wait.Until(() => NetworkedItem.TryGet(boxId, out box) && box != null && NetworkedItem.TryGet(itemId, out item) && item != null, 20f, "the container and the item to arrive");
                if (Hands.Held != null) yield return Hands.Drop();
                yield return LocalPlayer.GoTo(item);
                yield return Hands.Pickup(itemId);
                inv.AddItemToInventory(item.gameObject);
                for (int i = 0; i < 60; i++) yield return null;
                yield return LocalPlayer.GoTo(box);
                yield return Hands.Pickup(boxId);
                var c = box.GetComponentInChildren<DV.Items.ItemContainer>(true);
                c.ToggleContainerAccess();
                yield return Wait.Until(() => CUI.Active, 10f, "the container view");
                uint seq = CUI.MutationSequence;
                // Put away, it's a stand-in on a client (D14).
                if (c.AddItem(PlayerInventory.SlotObject(itemId), -1)) throw new Exception("the deposit was not intercepted");
                yield return Wait.Until(() => CUI.MutationSequence != seq && CUI.LastMutation != null, 10f, "the deposit's result");
                if (!CUI.LastMutation.Accepted) throw new Exception("deposit rejected: " + CUI.LastMutation.Detail);
                c.ToggleContainerAccess();
                for (int i = 0; i < 30; i++) yield return null;
            }

            /// <summary>Host: the client's player browses the container as if the load hadn't run.
            /// Result: the status and reason.</summary>
            [Step]
            public static string BrowseBeforeTheLoad(ushort box)
            {
                var player = Multiplayer.Server.Session.NetworkServer.Instance.ServerPlayers
                    .First(p => p.Peer != Multiplayer.Server.Session.NetworkServer.Instance.SelfPeer);
                ServerLedger.loaded = false;
                try
                {
                    ContainerOperationResult result = ContainerServer.Browse(player, box, 0, 8, out _);
                    return $"{result.Status} {result.Reason}";
                }
                finally
                {
                    ServerLedger.loaded = true;
                }
            }

            /// <summary>Host: a manual save whose ledger row for the item puts it past its container's
            /// end, as a capacity change leaves it. Result: the row as saved.</summary>
            [Step]
            public static IEnumerator SaveWithBadRow(Step<string> step, string itemId) => SaveEdits.SaveWith(step, root =>
            {
                if (SaveEdits.LedgerRow(root, itemId)?["where"] is not JObject where)
                    return "";
                where["slot"] = 999;
                return SaveEdits.LedgerRow(root, itemId).ToString(Formatting.None);
            });

            /// <summary>Host: the reason of the item's Lost and Found entry, or "".</summary>
            [Step]
            public static string LostReason(string itemId) =>
                Guid.TryParse(itemId, out Guid id) && LostAndFoundServer.TryGetEntry(id, out var entry) ? entry.Reason.ToString() : "";

            /// <summary>Host: the saved ledger homes of the items, in order, comma-separated.</summary>
            [Step]
            public static string SavedHomes(string first, string second)
            {
                JObject root = SaveEdits.Saved();
                string Home(string id) => (string)SaveEdits.LedgerRow(root, id)?["where"]?["kind"] ?? "none";
                return Home(first) + "," + Home(second);
            }
        }
    }
}
