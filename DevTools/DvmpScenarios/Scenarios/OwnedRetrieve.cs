using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Packets.Clientbound;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Retrieval from the owned-items list (owned-items.md, "Retrieval", PR 6b). The client owns a
    /// mug on the ground, one in a loco's cab, and one inside its toolbox lying in the world. It
    /// retrieves each from its list: the ground and cab mugs are in its Lost and Found at once, and
    /// for the boxed mug the whole toolbox goes, the mug still inside. Retrieving a mug from its own
    /// inventory is refused (nothing to do). On a player host, the host's own player holds the
    /// client's lantern in hand and the client retrieves it: the lantern leaves the host's hand and
    /// is in the client's Lost and Found.
    /// </summary>
    [Scenario("owned-retrieve", "The client retrieves its ground, cab and boxed mugs from its owned-items list into Lost and Found (the toolbox whole), is refused for one in its inventory, and on a player host takes its lantern back out of the host's hand")]
    [Budget(600)]
    public sealed class OwnedRetrieve : Scenario<OwnedRetrieve.Options>
    {
        public sealed class Options
        {
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(10000);   // a just-joined client is still receiving its items
            LostAndFound.Policy old = await pair.Host.Run(() => LostAndFound.InGame.SetPolicy(new LostAndFound.Policy { Owner = 5000, Nearby = 5000, Grace = 900 }));
            pair.Cleanup(() => pair.Host.Run(() => LostAndFound.InGame.SetPolicy(old)));
            string client = await pair.Host.Run(() => Owned.ClientIdentity());
            string loco = await pair.Host.Run<string>(s => Cars.SpawnCarNear(s, "LocoDE2", "client"));
            pair.Cleanup(() => pair.Host.Run<string[]>(s => Cars.Delete(s, new[] { loco })));

            ushort ground = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(ground), 60);
            ushort cab = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run(() => LostAndFound.InGame.Claim(cab), 60);
            await pair.Client.Run<float>(s => Items.CarryIntoCab(s, loco, cab, "drop", 90), 90);
            await pair.Client.Run<Abs>(s => Walk.Away(s, 0, 8));
            ushort box = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "ItemContainerToolbox", "client"));
            ushort boxed = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            string boxedId = await pair.Host.Run(() => Owned.IdOf(boxed));
            DepositRefused.Deposit deposit = await pair.Client.Run<DepositRefused.Deposit>(s => DepositRefused.InGame.TryDeposit(s, box, boxed), 120);
            Require.That(deposit.Accepted, "the mug deposited into the client's toolbox", deposit.ToString());
            ushort kept = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Mug", "client"));
            await pair.Client.Run<int>(s => Items.Stash(s, kept, false), 60);
            await Task.Delay(2000);

            foreach ((ushort net, string what) in new[] { (ground, "ground"), (cab, "cab") })
            {
                string id = await pair.Host.Run(() => Owned.IdOf(net));
                Result r = await Retrieve(pair, client, id);
                Expect.That(r.Accepted, $"the {what} mug's retrieval is accepted", r.Reason);
                OwnerInfo now = await pair.Host.Run(() => Ownership.ById(id));
                Expect.That(now.Lost && now.LostEntry == id, $"the {what} mug is in the client's Lost and Found", now.ToString());
            }

            string boxId = await pair.Host.Run(() => Owned.IdOf(box));
            Result b = await Retrieve(pair, client, boxedId);
            Expect.That(b.Accepted, "the boxed mug's retrieval is accepted", b.Reason);
            OwnerInfo boxNow = await pair.Host.Run(() => Ownership.ById(boxId));
            Expect.That(boxNow.Lost, "the whole toolbox is in the client's Lost and Found", boxNow.ToString());
            LedgerEntry inBox = await pair.Host.Run(() => Owned.Entry(boxedId));
            Expect.Equal("Container", inBox.Place, "where the boxed mug is (still in the toolbox)");

            string keptId = await pair.Host.Run(() => Owned.IdOf(kept));
            Result k = await Retrieve(pair, client, keptId);
            Expect.That(!k.Accepted, "retrieving a mug from the client's own inventory is refused", k.Reason);
            Expect.Equal(OwnedItemRetrievalReasons.NothingToDo, k.Reason, "why");

            if (pair.Dedicated) return;
            ushort lantern = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, "Lantern", "host"));
            await pair.Host.Run(() => Ownership.Give(lantern, "client"));
            await pair.Host.Run(() => Items.Pickup(lantern), 60);
            Require.Equal(lantern, await pair.Host.Run(() => Items.Held()), "the lantern in the host's hand");
            string lanternId = await pair.Host.Run(() => Owned.IdOf(lantern));
            Result l = await Retrieve(pair, client, lanternId);
            Expect.That(l.Accepted, "the client's retrieval of its lantern from the host's hand is accepted", l.Reason);
            await Task.Delay(1000);
            Expect.Equal((ushort)0, await pair.Host.Run(() => Items.Held()), "what the host holds after the retrieval");
            OwnerInfo lanternNow = await pair.Host.Run(() => Ownership.ById(lanternId));
            Expect.That(lanternNow.Lost && lanternNow.Owner == client, "the lantern is in the client's Lost and Found", lanternNow.ToString());
        }

        // The client asks for its list, picks the item's row by the handle the server gave it, and retrieves it.
        private static async Task<Result> Retrieve(Pair pair, string client, string itemId)
        {
            uint handle = await pair.Host.Run(() => InGame.HandleOf(client, itemId));
            if (handle == 0) return new Result { Reason = "not in the server's list" };
            return await pair.Client.Run<Result>(s => InGame.Retrieve(s, handle), 30);
        }

        public sealed class Result
        {
            public bool Accepted;
            public string Reason = "";
        }

        /// <summary>The server's refusal reasons, as Run can read them.</summary>
        public static class OwnedItemRetrievalReasons
        {
            public const string NothingToDo = "nothing-to-retrieve";
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Host: the handle of the item's row in the owner's list (0 when it isn't listed).</summary>
            [Step]
            public static uint HandleOf(string owner, string itemId)
            {
                ServerPlayer player = NetworkServer.Instance.ServerPlayers.First(p => p.Guid.ToString("D") == owner);
                return OwnedItemRows.For(player).FirstOrDefault(r => r.Id.ToString("D") == itemId)?.Handle ?? 0;
            }

            /// <summary>Client: retrieves the row's item and waits for the server's answer.</summary>
            [Step]
            public static IEnumerator Retrieve(Step<Result> step, uint handle)
            {
                ClientboundOwnedItemRetrieveResultPacket result = null;
                void Got(ClientboundOwnedItemRetrieveResultPacket p) { if (p.Handle == handle) result = p; }
                ItemClient.Current.OwnedItemRetrieveCompleted += Got;
                try
                {
                    ItemClient.Current.RequestOwnedItemRetrieval((uint)UnityEngine.Random.Range(1000, 1000000), handle);
                    float t0 = Time.realtimeSinceStartup;
                    while (result == null && Time.realtimeSinceStartup - t0 < 15f) yield return null;
                }
                finally
                {
                    ItemClient.Current.OwnedItemRetrieveCompleted -= Got;
                }
                if (result == null) throw new Exception("no answer to the retrieval of " + handle);
                for (int i = 0; i < 30; i++) yield return null;
                step.Result = new Result { Accepted = result.Accepted, Reason = result.RejectionReason ?? "" };
            }
        }
    }
}
