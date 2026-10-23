using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items.LostAndFound;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A save lands while Lost and Found sweeps up an offline owner's items (owned-items.md, PR 3).
    /// Mugs for a player who isn't here lie on the ground. Everyone walks away, and the host saves
    /// every quarter second until the sweep has started (the last save is mid-sweep when the sweep
    /// spans frames). The games restart on that save. Every mug must come back exactly once, still
    /// the absent player's with the absent number, either in the world or in their Lost and Found
    /// under its own id, with no ledger audit problems. Then the sweep finishes after the restart.
    /// </summary>
    [Scenario("offline-sweep-mid-save", "A save during the sweep of an absent owner's mugs: each comes back once, theirs, in the world or their Lost and Found, and the sweep finishes after the restart")]
    [Needs(Need.RestartsGames)]
    [Budget(900)]
    public sealed class OfflineSweepMidSave : Scenario<OfflineSweepMidSave.Options>
    {
        public sealed class Options
        {
            /// <summary>The save to reload.</summary>
            public string Session = "DVMP Test Career 2";
            /// <summary>How many mugs the absent player has.</summary>
            public int Count = 6;
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its restored items
            var policy = new LostAndFound.Policy { Owner = 50, Nearby = 100, Grace = 1 };
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(policy));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string owner = System.Guid.NewGuid().ToString("D");
            var ids = new string[o.Count];
            var nets = new ushort[o.Count];
            for (int i = 0; i < o.Count; i++)
            {
                nets[i] = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
                ids[i] = (await pair.Host.Run(() => Ownership.Give(nets[i], owner))).Id;
            }
            Report.Note($"{o.Count} mugs for absent {owner}");

            await pair.Client.Run<Abs>(s => Walk.Away(s, 400, 0));
            if (!pair.Dedicated) await pair.Host.Run<Abs>(s => Walk.Away(s, 400, 20));
            string saved = await pair.Host.Run<string>(s => InGame.SaveDuringSweep(s, nets, 30f), 60);
            Report.Note("saves: " + saved);
            await pair.Restart(o.Session);
            await Task.Delay(15000);

            int lost = 0;
            foreach (string id in ids)
            {
                OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
                if (!Expect.That(now.Exists, "the mug is there after the restart", id)) continue;
                Items.Track(now.NetId);
                if (now.Lost) lost++;
                Expect.Equal(owner, now.Owner, $"mug {id}'s owner after the restart");
                Expect.Equal(AbsentOwner.AbsentNumber, now.Number, $"mug {id}'s owner number after the restart");
                Expect.That(now.Lost ? now.LostEntry == id : now.Placement == "World", "the mug is in the world or in Lost and Found under its own id", now.ToString());
            }
            Report.Note($"after the restart {lost} of {ids.Length} in Lost and Found");
            Expect.Empty(await pair.Host.Run<string[]>(s => Owned.AuditFor(s, ids)), "ledger audit problems for the mugs after the restart");

            await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(policy));
            foreach (string id in ids)
                await Until.Value(() => pair.Host.Run(() => Ownership.ById(id)), v => !v.Exists || v.Lost, 30, "the mug collected after the restart");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>
            /// Host: saves every quarter second until the sweep has collected some of the items (or
            /// all, or <paramref name="seconds"/> pass), then gives the last save 3 s to be written.
            /// Result: how many saves, and when the last one fell.
            /// </summary>
            [Step]
            public static IEnumerator SaveDuringSweep(Step<string> step, ushort[] ids, float seconds)
            {
                float t0 = Time.realtimeSinceStartup;
                int saves = 0;
                string when = "before the sweep (it never started)";
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    int lost = ids.Count(NetworkedLostAndFoundManager.Contains);
                    SaveGameManager.Instance.Save(DV.Common.SaveType.Manual);
                    saves++;
                    if (lost == ids.Length) { when = "after the sweep (it took one step)"; break; }
                    if (lost > 0) { when = $"mid-sweep ({lost} of {ids.Length} collected)"; break; }
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                yield return new WaitForSeconds(3);
                step.Result = $"{saves}, the last {when}";
            }
        }
    }
}
