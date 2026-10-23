using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Client.Items.OwnedList;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// The owned-items list (owned-items.md, PR 6a). The client owns mugs in its inventory, on the
    /// ground, in a loco's cab, in its Lost and Found and inside its toolbox, and one it owned has
    /// been destroyed. The server's list for the client has each live mug once, worded by where it
    /// is (your inventory, on the ground, in the loco, in Lost and Found, in the toolbox), and not
    /// the destroyed one. The list the client receives has the same rows.
    /// </summary>
    [Scenario("owned-list", "The client's owned-items list has each of its mugs once, worded by where it is (inventory, ground, a cab, Lost and Found, a toolbox), not a destroyed one, and the client receives the same rows")]
    [Budget(600)]
    public sealed class OwnedList : Scenario<OwnedList.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));

            ushort kept = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, kept, false), 60);
            ushort ground = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(ground), 60);
            ushort cab = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(cab), 60);
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, cab, "drop", 90), 90);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 0, 8));
            ushort lost = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(lost), 60);
            Require.That(await pair.Host.Run<bool>(s => Ownership.Collect(s, lost, "SaveRecovery")), "the Lost and Found mug collected");
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "ItemContainerToolbox", "client"));
            ushort boxed = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            string boxedId = await pair.Host.Run(() => Owned.IdOf(boxed));   // one id for life, through the container too
            DepositRefused.Deposit deposit = await pair.Client.Run<DepositRefused.Deposit>(s => DepositRefused.InGame.TryDeposit(s, box, boxed), 120);
            Require.That(deposit.Accepted, "the mug deposited into the client's toolbox", deposit.ToString());
            ushort gone = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(gone), 60);
            string goneId = await pair.Host.Run(() => Owned.IdOf(gone));
            await pair.Host.Run<ushort[]>(s => Items.Destroy(s, new[] { gone }));
            await Task.Delay(2000);

            string[] ids = new string[5];
            ushort[] nets = { kept, ground, cab, lost };
            for (int i = 0; i < nets.Length; i++) ids[i] = await pair.Host.Run(() => Owned.IdOf(nets[i]));
            ids[4] = boxedId;
            string[] wanted = { "in your inventory", "on the ground", "in " + loco, "in Lost and Found", "in " };
            string[] names = { "stashed", "ground", "cab", "Lost and Found", "boxed" };

            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            Rows server = await pair.Host.Run(() => InGame.ServerRows(client));
            Report.Note($"server rows for the client: {server.Ids.Length}");
            for (int i = 0; i < ids.Length; i++)
            {
                int at = Array.IndexOf(server.Ids, ids[i]);
                if (!Expect.That(at >= 0 && server.Ids.Count(x => x == ids[i]) == 1, $"the {names[i]} mug is listed once", ids[i])) continue;
                Report.Note($"{names[i]} mug: {server.Wheres[at]}");
                Expect.That(server.Wheres[at].StartsWith(wanted[i], StringComparison.Ordinal), $"the {names[i]} mug's place", server.Wheres[at]);
            }
            Expect.That(!server.Ids.Contains(goneId), "the destroyed mug isn't listed", goneId);

            Rows received = await pair.Client.Run<Rows>(s => InGame.ClientRows(s), 30);
            Expect.Equal(server.Ids.Length, received.Wheres.Length, "rows the client received");
            Expect.Equal(string.Join("|", server.Wheres.OrderBy(w => w)), string.Join("|", received.Wheres.OrderBy(w => w)), "the client's rows' places");
        }

        public sealed class Rows
        {
            public string[] Ids = new string[0], Wheres = new string[0];
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the client's owned-items rows as the server builds them.</summary>
            [Step]
            public static Rows ServerRows(string owner)
            {
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == owner);
                var rows = OwnedItemRows.For(player);
                return new Rows { Ids = rows.Select(r => r.Id.ToString("D")).ToArray(), Wheres = rows.Select(r => r.Where).ToArray() };
            }

            /// <summary>Client: asks for its owned-items list and waits for the answer. Result: its rows' places.</summary>
            [Step]
            public static IEnumerator ClientRows(Step<Rows> step)
            {
                uint id = (uint)UnityEngine.Random.Range(1000, 1000000);
                ItemClient.Current.RequestOwnedItems(id);
                float t0 = Time.realtimeSinceStartup;
                while (OwnedItemsList.RequestId != id && Time.realtimeSinceStartup - t0 < 20f) yield return null;
                if (OwnedItemsList.RequestId != id) throw new Exception("the owned-items list never came");
                step.Result = new Rows { Wheres = OwnedItemsList.Rows.Select(r => r.Where).ToArray() };
            }
        }
    }
}
