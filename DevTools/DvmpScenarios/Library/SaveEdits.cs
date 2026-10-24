// A save written with a change on top of what the mod wrote: a bad row, an old key, a missing
// prefab, as a scenario needs one in the save it then restarts from.
using System;
using System.Collections;
using HarmonyLib;
using Multiplayer.Server.Career;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    public static class SaveEdits
    {
        private static Func<JObject, string> edit;
        private static string result = "";
        private static bool patched;

        /// <summary>
        /// Host: a manual save whose Multiplayer root <paramref name="change"/> edits after the mod has
        /// written it, once. Result: what the change says it did ("" when it didn't run).
        /// </summary>
        public static IEnumerator SaveWith(Step<string> step, Func<JObject, string> change)
        {
            if (!patched)
            {
                new Harmony("dvmp.scenarios.save-edits").Patch(
                    AccessTools.Method(typeof(NetworkedSaveGameManager), nameof(NetworkedSaveGameManager.Server_UpdateInternalData)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(SaveEdits), nameof(AfterSave))));
                patched = true;
            }
            edit = change;
            result = "";
            yield return Saves.Manual();
            edit = null;
            step.Result = result;
        }

        private static void AfterSave(SaveGameData data)
        {
            if (edit == null)
                return;
            JObject root = data.GetJObject("Multiplayer") ?? new JObject();
            result = edit(root) ?? "";
            data.SetJObject("Multiplayer", root);
            edit = null;
        }

        /// <summary>The saved <c>Multiplayer.Items</c> ledger row of an item, or null.</summary>
        public static JObject LedgerRow(JObject root, string id) =>
            (root?["Items"]?["ledger"] as JArray)?.FirstOrDefaultObject(row => (string)row["id"] == id);

        /// <summary>The saved <c>Multiplayer.Items</c> state row of an owned item, or null.</summary>
        public static JObject StateRow(JObject root, string id) => root?["Items"]?["states"]?[id] as JObject;

        /// <summary>The Multiplayer root as the last save wrote it.</summary>
        public static JObject Saved() => SaveGameManager.Instance.data.GetJObject("Multiplayer");

        private static JObject FirstOrDefaultObject(this JArray array, Func<JObject, bool> match)
        {
            foreach (JToken token in array)
                if (token is JObject row && match(row))
                    return row;
            return null;
        }
    }
}
