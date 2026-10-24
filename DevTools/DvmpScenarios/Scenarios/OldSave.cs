using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A career saved before <c>Multiplayer.Items</c> (D1, OI-PER-21; docs/design/items/save-load.md,
    /// "Old saves"). The client owns a mug; the host saves with the items key taken out and an old
    /// <c>WorldItemsV1</c> key put in its place, holding the mug's old record. After the restart the
    /// world and the career load, the old key isn't read (the mug isn't in the ledger), the load says
    /// so once, and the next save has <c>Items</c> and none of the old keys. After a second restart
    /// nothing is said about old keys.
    /// </summary>
    [Scenario("old-save", "A save with only the old item keys loads, starts items fresh, logs it once, and isn't written with them again")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class OldSave : Scenario<OldSave.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
        }

        private const string Said = "saved before Multiplayer.Items";

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            ushort mug = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(mug), 60);
            string id = await pair.Host.Run(() => Owned.IdOf(mug));
            Require.That((await pair.Host.Run(() => Owned.Entry(id))).Live, "the client's mug is in the ledger", id);

            string edited = await pair.Host.Run<string>(s => InGame.SaveAsOld(s, id), 60);
            Report.Note("saved as an old career: " + edited);
            Require.That(edited.Length > 0, "the save was made an old one", edited);

            await pair.Restart(o.Session);
            await Task.Delay(15000);
            int said = (await pair.Host.LogsSince(0, "warning")).Count(l => l.Message.Contains(Said));
            Expect.Equal(1, said, "the load says once that the save is from before Multiplayer.Items");
            Expect.That(!(await pair.Host.Run(() => Owned.Entry(id))).Known, "the old key's mug isn't in the ledger (items start fresh)", id);
            Expect.That(await pair.Host.Run(() => Owned.Loaded()), "the load ran");

            await pair.Host.Run(() => Saves.Manual());
            string keys = await pair.Host.Run(() => InGame.ItemKeys());
            Report.Note("the next save's item keys: " + keys);
            Expect.Equal("Items", keys, "the next save's item keys (the old ones aren't written again)");

            await pair.Restart(o.Session);
            await Task.Delay(15000);
            Expect.Zero((await pair.Host.LogsSince(0, "warning")).Count(l => l.Message.Contains(Said)),
                "lines about old keys after a restart from a save the build wrote");
        }

        // ---- runs inside the games
        public static class InGame
        {
            private static readonly string[] OldKeys = { "WorldItemsV1", "OwnedItemsV1", "LostAndFound", "ColdContainers" };

            /// <summary>Host: a manual save with no Items key, and the item's record in WorldItemsV1 as
            /// an older build wrote it. Result: the record.</summary>
            [Step]
            public static IEnumerator SaveAsOld(Step<string> step, string itemId) => SaveEdits.SaveWith(step, root =>
            {
                root.Remove("Items");
                JObject record = new()
                {
                    ["persistentItemId"] = itemId,
                    ["prefabName"] = "Mug",
                    ["placement"] = 0,
                    ["ownerIdentity"] = Owned.ClientIdentity(),
                    ["position"] = new JArray(0f, 0f, 0f)
                };
                root["WorldItemsV1"] = new JArray(record);
                return record.ToString(Newtonsoft.Json.Formatting.None);
            });

            /// <summary>Host: the item keys the last save wrote, comma-separated.</summary>
            [Step]
            public static string ItemKeys()
            {
                JObject root = SaveEdits.Saved();
                return string.Join(",", new[] { "Items" }.Concat(OldKeys).Where(key => root?[key] != null));
            }
        }
    }
}
