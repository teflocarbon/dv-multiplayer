using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Rows that won't load (D9, OI-LED-16, OI-PER-15; docs/design/items/save-load.md). The client owns
    /// a mug in its inventory and a flashlight on the ground. The host saves with the mug's prefab
    /// renamed to one no item has (an item mod removed) and the flashlight's state made unreadable.
    /// After a restart the mug is <c>Unloadable</c>, its slot free (a lantern put there keeps it at
    /// the next load), and its rows are written back exactly as they were, through two saves and
    /// restarts; the flashlight comes back fresh, with its
    /// id, in the client's Lost and Found (<c>SaveRecovery</c>), with an error logged.
    /// </summary>
    [Scenario("unloadable-item", "A row whose prefab is gone is kept as it was through two saves; a state that won't load comes back fresh in Lost and Found")]
    [Needs(Need.RestartsGames)]
    [Budget(1200)]
    public sealed class UnloadableItem : Scenario<UnloadableItem.Options>
    {
        public sealed class Options
        {
            /// <summary>The item whose prefab goes missing.</summary>
            public string Gone = "Mug";
            /// <summary>The item whose state is made unreadable: one with saved state.</summary>
            public string Corrupt = "Flashlight";
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            ushort gone = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Gone, "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, gone, false), 60);
            ushort corrupt = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Corrupt, "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(corrupt), 60);
            string goneId = await pair.Host.Run(() => Owned.IdOf(gone));
            string corruptId = await pair.Host.Run(() => Owned.IdOf(corrupt));
            LedgerEntry goneBefore = await pair.Host.Run(() => Owned.Entry(goneId));
            Report.Note($"before: {goneBefore}");
            Require.That(goneBefore.Live && goneBefore.Place == "Slot", "the mug is in the client's inventory", goneBefore.ToString());

            string edited = await pair.Host.Run<string>(s => InGame.SaveBroken(s, goneId, corruptId), 60);
            Report.Note("saved broken: " + edited);
            Require.That(edited.StartsWith("ok"), "the save was broken as planned", edited);

            await pair.Restart(o.Session);
            await Task.Delay(15000);
            LedgerEntry goneAfter = await pair.Host.Run(() => Owned.Entry(goneId));
            LedgerEntry corruptAfter = await pair.Host.Run(() => Owned.Entry(corruptId));
            Report.Note($"after the restart: {goneAfter}; {corruptAfter}");
            Expect.That(goneAfter.Live && goneAfter.Place == "Unloadable", "the mug whose prefab is gone is Unloadable", goneAfter.ToString());
            Expect.That(corruptAfter.Live && corruptAfter.Place == "LostAndFound", "the flashlight whose state won't load is in Lost and Found", corruptAfter.ToString());
            Expect.Equal("SaveRecovery", await pair.Host.Run(() => ItemBugContainerSaveRefused.InGame.LostReason(corruptId)), "why the flashlight is there");
            Expect.That((await pair.Host.LogsSince(0, "error")).Any(l => l.Message.Contains(corruptId)), "the unreadable state is logged as an error");

            // Its slot is free: an item put there keeps it at the next load, though the mug's row
            // still names that slot.
            ushort taker = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, taker, false), 60);
            Expect.That(await pair.Client.Run<bool>(s => InventorySlots.InGame.Move(s, taker, goneBefore.Slot)),
                "the client put a lantern in the mug's old slot", $"slot {goneBefore.Slot}");
            string takerId = await pair.Host.Run(() => Owned.IdOf(taker));

            await pair.Host.Run(() => Saves.Manual());
            string second = await pair.Host.Run(() => InGame.SavedRows(goneId));
            Report.Note("the mug's rows, saved after the load: " + second);
            Expect.Equal(edited.Substring(3), second, "the mug's rows are written back as they were");

            await pair.Restart(o.Session);
            await Task.Delay(15000);
            Expect.Equal("Unloadable", (await pair.Host.Run(() => Owned.Entry(goneId))).Place, "the mug after a second restart");
            LedgerEntry takerAfter = await pair.Host.Run(() => Owned.Entry(takerId));
            Expect.Equal($"Slot {goneBefore.Holder} slot {goneBefore.Slot}", takerAfter.Where,
                "the lantern in the mug's old slot, after a second restart");
            await pair.Host.Run(() => Saves.Manual());
            Expect.Equal(second, await pair.Host.Run(() => InGame.SavedRows(goneId)), "the mug's rows after a second save");
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string NoSuchPrefab = "dvmpNoSuchItem";

            /// <summary>
            /// Host: a manual save with the first item's prefab renamed to one no item has, in its
            /// ledger and state rows, and the second item's state unreadable (its id key not a string).
            /// Result: "ok " and the first item's rows as saved.
            /// </summary>
            [Step]
            public static IEnumerator SaveBroken(Step<string> step, string goneId, string corruptId) => SaveEdits.SaveWith(step, root =>
            {
                JObject ledger = SaveEdits.LedgerRow(root, goneId), state = SaveEdits.StateRow(root, goneId);
                JObject corrupt = SaveEdits.StateRow(root, corruptId)?["state"] as JObject;
                if (ledger == null || state == null)
                    return "the mug's rows aren't in the save";
                if (corrupt == null)
                    return "the flashlight's row has no state to break";
                ledger["prefab"] = NoSuchPrefab;
                state["prefab"] = NoSuchPrefab;
                corrupt["dvmpPersistentWorldItemId"] = new JArray(1, 2);
                return "ok " + Rows(root, goneId);
            });

            /// <summary>Host: the item's ledger and state rows as the last save wrote them.</summary>
            [Step]
            public static string SavedRows(string id) => Rows(SaveEdits.Saved(), id);

            private static string Rows(JObject root, string id) =>
                (SaveEdits.LedgerRow(root, id)?.ToString(Formatting.None) ?? "none") + " " +
                (SaveEdits.StateRow(root, id)?.ToString(Formatting.None) ?? "none");
        }
    }
}
