using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.SaveLoad;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The one-home check runs before every save (D7, OI-LED-15; docs/design/items/ledger.md, "The
    /// invariant check"): the host saves, and the check ran once more under "before-save"
    /// (<c>ServerLedger.ChecksRun</c>), and had run by the time the save wrote its first rows
    /// (<c>ItemSaveLoad.WriteOwned</c>, watched by a Harmony prefix), so a Release build's repair
    /// comes before the rows it would fix. Without the fix (<c>WriteSave</c> not calling
    /// <c>CheckInvariant("before-save")</c>) the count stays where it was and both checks fail. Works
    /// the same on a player host and a dedicated one: only the host saves.
    /// </summary>
    [Scenario("save-invariant", "The one-home check runs before every save, before the save writes its rows (OI-LED-15)")]
    [Budget(120)]
    public sealed class SaveInvariant : Scenario<SaveInvariant.Options>
    {
        public sealed class Options
        {
            /// <summary>How many saves to make; each must run the check once more.</summary>
            public int Count = 2;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Require.That(await pair.Host.Run(() => Owned.Loaded()), "the host's ledger is loaded (a save before the load writes nothing new)");
            await pair.Host.Run(() => InGame.Watch(true));
            pair.Cleanup(() => pair.Host.Run(() => InGame.Watch(false)));
            for (int i = 1; i <= o.Count; i++)
            {
                Seen before = await pair.Host.Run(() => InGame.Look());
                await pair.Host.Run(() => Saves.Manual());
                Seen after = await pair.Host.Run(() => InGame.Look());
                Report.Note($"save {i}: before-save checks {before.Checks} -> {after.Checks}; at the rows' write {after.AtWrite}; " +
                            $"rows written {after.Writes - before.Writes} time(s); violations then: {after.Violations}");
                Require.AtLeast(after.Writes - before.Writes, 1, $"save {i}: the save wrote its owned rows");
                Expect.AtLeast(after.Checks - before.Checks, 1, $"save {i}: one-home checks run before the save");
                Expect.AtLeast(after.AtWrite - before.Checks, 1, $"save {i}: one-home checks run by the time the save wrote its rows");
            }
        }

        public sealed class Seen
        {
            /// <summary>ServerLedger.ChecksRun("before-save") now, and as it was when the save last wrote its owned rows (-1 before any).</summary>
            public int Checks, AtWrite = -1;
            /// <summary>How many times the save wrote its owned rows since the watch began.</summary>
            public int Writes;
            public string Violations = "";
        }

        // ---- runs inside the games
        public static class InGame
        {
            private const string HarmonyId = "dvmp.scenarios.save-invariant";
            private const string When = "before-save";
            private static int atWrite = -1, writes;

            /// <summary>Host: watches the save's first rows being written (on), or stops (off).</summary>
            [Step]
            public static void Watch(bool on)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.UnpatchAll(HarmonyId);
                atWrite = -1;
                writes = 0;
                if (on)
                    harmony.Patch(AccessTools.Method(typeof(ItemSaveLoad), "WriteOwned"),
                        prefix: new HarmonyMethod(typeof(InGame).GetMethod(nameof(WriteOwnedPrefix))));
            }

            public static void WriteOwnedPrefix()
            {
                atWrite = ServerLedger.ChecksRun(When);
                writes++;
            }

            /// <summary>Host: the before-save checks run so far, and what the watch saw.</summary>
            [Step]
            public static Seen Look() => new()
            {
                Checks = ServerLedger.ChecksRun(When),
                AtWrite = atWrite,
                Writes = writes,
                Violations = ServerLedger.LastViolations.Count == 0 ? "none" : string.Join("; ", ServerLedger.LastViolations.Select(v => v.ToString()))
            };
        }
    }
}
