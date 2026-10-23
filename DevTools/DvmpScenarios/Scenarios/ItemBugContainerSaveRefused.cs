using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dvmp.Agent;
using Dvmp.Lib;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Shared.Items.LiveState;
using Newtonsoft.Json.Linq;
using UnityEngine;
using CUI = Multiplayer.Client.Items.Containers.ColdContainerUiIntegration;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Item bug F18 (docs/research/item-bug-review.md). [KnownBug] until R4 of the item refactor
    /// (docs/design/item-refactor.md) replaces the container save with rows that stand alone; branch
    /// items/container-save-refused has a fix for today's save, not merged. A cold-container save the server
    /// refuses (a stored item's slot out of range after a capacity change, an older codec, a
    /// corrupt blob, nesting deeper than 8) left the graph empty, and the next save (any join,
    /// leave or autosave) wrote that empty graph over it: every player's stored items were gone.
    /// The run stores a mug, saves, makes the saved blob unimportable the way a capacity change
    /// does (one item's slot past the end), has the server import it again, and saves: the blob
    /// must still hold the stored items, and every container operation must be refused meanwhile
    /// (a deposit would go into a graph that is never saved). Cleanup puts the original blob back.
    /// </summary>
    [Scenario("item-bug-container-save-refused", "A cold-container save the server can't import is kept as it was, never overwritten with an empty one, and containers are refused meanwhile (F18)")]
    [KnownBug("F18")]
    [Budget(600)]
    public sealed class ItemBugContainerSaveRefused : Scenario<ItemBugContainerSaveRefused.Options>
    {
        public sealed class Options
        {
            /// <summary>The container's prefab.</summary>
            public string Container = "ItemContainerToolbox";
            /// <summary>The item to store.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Container, "client"));
            ushort item = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            await pair.Client.Run(() => InGame.Deposit(box, item), 120);
            await pair.Host.Run(() => Saves.Manual());
            Blob saved = await pair.Host.Run(() => InGame.Read());
            Report.Note($"saved: {saved.Items} stored item(s), {saved.Containers} container(s)");
            Require.That(saved.Items >= 1, "the save holds the stored mug", saved.Items + " items");
            pair.Cleanup(() => pair.Host.Run(() => InGame.Restore(saved.Encoded)));

            string refused = await pair.Host.Run(() => InGame.MakeUnimportable());
            Report.Note("the server's import of the changed blob: " + refused);
            if (!refused.StartsWith("refused")) throw new HarnessException("the server accepted the changed blob, so nothing was tested: " + refused);
            string browse = await pair.Host.Run(() => InGame.TryBrowse(box));
            Report.Note("browsing the client's container meanwhile: " + browse);
            Expect.That(browse.Contains("container-save-unreadable"), "containers are refused while the save's containers can't be read", browse);
            await pair.Host.Run(() => Saves.Manual());
            Blob after = await pair.Host.Run(() => InGame.Read());
            Report.Note($"saved again: {after.Items} stored item(s), {after.Containers} container(s)");
            Expect.That(after.Items >= saved.Items, "the save still holds every stored item after a save the server couldn't import",
                $"{after.Items} of {saved.Items}");
        }

        public sealed class Blob
        {
            public int Items, Containers;
            public string Encoded;
        }

        // ---- runs inside the games
        public static class InGame
        {
            static JObject Root() => SaveGameManager.Instance.data.GetJObject("Multiplayer");

            // GetJObject hands out a copy: a change is written back whole.
            static void SetBlob(string encoded)
            {
                JObject root = Root();
                root["ColdContainers"] = encoded;
                SaveGameManager.Instance.data.SetJObject("Multiplayer", root);
            }

            static void Import()
            {
                typeof(NetworkedColdContainerManager).GetMethod("TryImportSave", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, null);
            }

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
                if (c.AddItem(item.gameObject, -1)) throw new Exception("the deposit was not intercepted");
                yield return Wait.Until(() => CUI.MutationSequence != seq && CUI.LastMutation != null, 10f, "the deposit's result");
                if (!CUI.LastMutation.Accepted) throw new Exception("deposit rejected: " + CUI.LastMutation.RejectionReason);
                c.ToggleContainerAccess();
                for (int i = 0; i < 30; i++) yield return null;
            }

            /// <summary>Host: what the save's cold-container blob holds.</summary>
            [Step]
            public static Blob Read()
            {
                string encoded = (string)Root()?["ColdContainers"];
                if (string.IsNullOrEmpty(encoded)) return new Blob { Encoded = encoded ?? "" };
                if (!ColdContainerSnapshotCodec.TryDecode(Convert.FromBase64String(encoded), out ColdContainerGraphSnapshot snapshot, out string reason))
                    throw new Exception("the saved blob doesn't decode: " + reason);
                return new Blob { Items = snapshot.Items.Count, Containers = snapshot.Containers.Count, Encoded = encoded };
            }

            /// <summary>Host: puts one stored item's slot past its container's end in the save (as a
            /// capacity change leaves it), and has the server import the save again. Result: whether
            /// it was refused, and the graph's item count after.</summary>
            [Step]
            public static string MakeUnimportable()
            {
                string encoded = (string)Root()["ColdContainers"];
                ColdContainerSnapshotCodec.TryDecode(Convert.FromBase64String(encoded), out ColdContainerGraphSnapshot snapshot, out _);
                ColdStoredItemRecord stored = snapshot.Items.First();
                int capacity = snapshot.Containers.First(c => c.PersistentContainerId == stored.ParentContainerId).Capacity;
                stored.Slot = capacity + 5;
                SetBlob(Convert.ToBase64String(ColdContainerSnapshotCodec.Encode(snapshot)));
                NetworkedColdContainerManager.Clear();
                Import();
                int left = NetworkedColdContainerManager.Graph.ExportSnapshot().Items.Count;
                return (left == 0 ? "refused" : "accepted") + $": {left} item(s) in the graph after the import";
            }

            /// <summary>Host: the client's player browses the container, as its request does.
            /// Result: the status and reason.</summary>
            [Step]
            public static string TryBrowse(ushort box)
            {
                var player = Multiplayer.Server.Session.NetworkServer.Instance.ServerPlayers
                    .First(p => p.Peer != Multiplayer.Server.Session.NetworkServer.Instance.SelfPeer);
                ContainerOperationResult result = NetworkedColdContainerManager.Browse(player, box, 0, 8, out _);
                return $"{result.Status} {result.Reason}";
            }

            /// <summary>Host, cleanup: the original blob back, imported again, and saved.</summary>
            [Step]
            public static IEnumerator Restore(string encoded)
            {
                SetBlob(encoded);
                NetworkedColdContainerManager.Clear();
                Import();
                yield return Saves.Manual();
            }
        }
    }
}
