using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Agent;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// Red for every item someone else owns, online or not (refactor D3; wire.md, "Naming a player").
    /// The client puts three mugs in its inventory: one a player who isn't here owns, its own, and a
    /// found one nobody owns. Only the absent owner's is red. The server tells each player its
    /// relation to an item, never the owner, and the claim only to its claimant: the client gets no
    /// claim for the absent owner's mug, and on a player host the host's own player is told the
    /// client's mug is someone else's.
    /// </summary>
    [Scenario("red", "Red for every item someone else owns: in the client's inventory an absent owner's mug is red, its own and a found mug aren't; each player is told its relation, and only the claimant the claim")]
    public sealed class Red : Scenario<Red.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab.</summary>
            public string Prefab = "Mug";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            await Task.Delay(5000);   // a just-joined client is still receiving its restored items
            ushort absent = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            OwnerInfo given = await pair.Host.Run(() => Ownership.Give(absent, "absent"));
            ushort mine = await pair.Host.Run<ushort>(s => Items.SpawnPersonal(s, o.Prefab, "client"));
            await pair.Host.Run(() => Ownership.Give(mine, "client"));
            ushort found = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            Report.Note($"an absent owner's {o.Prefab} {absent} ({given.Owner}), the client's own {mine}, a found one {found}");

            foreach (ushort id in new[] { absent, mine, found })
                await pair.Client.Run<int>(s => Items.Stash(s, id, false), 60);
            await Task.Delay(2000);

            Told absentTold = await Check(pair, absent, "the absent owner's", "SomeoneElses", red: true);
            Expect.That(!absentTold.OwnerConnected && absentTold.Owner == given.Owner, "the absent owner's mug is still theirs, with no session", absentTold.Owner);
            Expect.That(!absentTold.ClientClaim, "the client isn't told the absent owner's claim (only its claimant is)");

            Told mineTold = await Check(pair, mine, "the client's own", "Mine", red: false);
            if (!pair.Dedicated)
            {
                Expect.Equal("SomeoneElses", mineTold.Host, "what the host's own player is told of the client's mug");
                Expect.That(!mineTold.HostClaim, "the host's own player isn't told the client's claim");
                Expect.Equal("SomeoneElses", mineTold.HostCopy, "the relation on the host's own copy of the client's mug");
            }

            Told foundTold = await Check(pair, found, "the found", "Nobodys", red: false);
            Expect.Equal(Guid.Empty.ToString("D"), foundTold.Owner, "the found mug's owner (nobody: finding isn't owning)");
            if (!pair.Dedicated)
                Expect.Equal("Nobodys", foundTold.Host, "what the host's own player is told of the found mug");

            foreach (ushort id in new[] { absent, mine, found })
                await pair.Client.Run(() => Owned.DropFromInventory(id), 60);
        }

        // What the client shows and what the server tells it, for one mug.
        private static async Task<Told> Check(Pair pair, ushort id, string whose, string relation, bool red)
        {
            Seen seen = await pair.Client.Run(() => InGame.Seen(id));
            Told told = await pair.Host.Run<Told>(s => InGame.Told(s, id));
            Report.Note($"{whose} mug {id}: the client has {seen}; the server tells it {told}");
            Expect.That(seen.InInventory, $"{whose} mug is in the client's inventory", seen.State);
            Expect.Equal(relation, told.Client, $"the relation the server tells the client of {whose} mug");
            Expect.Equal(relation, seen.Relation, $"the client's relation to {whose} mug");
            Expect.Equal(red, seen.Red, $"{whose} mug shows red in the client's inventory");
            return told;
        }

        public sealed class Seen
        {
            public string Relation = "", State = "";
            public bool Red, InInventory;
            public int ClaimSlot = -1;

            public override string ToString() => $"{Relation}{(Red ? ", red" : "")}, {State}, claim slot {ClaimSlot}";
        }

        public sealed class Told
        {
            /// <summary>The relations each player is told; "" for no host player.</summary>
            public string Client = "", Host = "", HostCopy = "";
            public bool ClientClaim, HostClaim, OwnerConnected;
            public string Owner = "";

            public override string ToString() => $"client {Client}{(ClientClaim ? " with the claim" : "")}" +
                (Host.Length > 0 ? $", host's player {Host}{(HostClaim ? " with the claim" : "")} (its copy {HostCopy})" : "");
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: its relation to the item, whether its inventory shows it red, and the claim slot it has.</summary>
            [Step]
            public static Seen Seen(ushort netId)
            {
                // Stored on a client, it's a stand-in with no object (D14).
                if (InventoryStandIn.TryGet(netId, out InventoryStandIn standIn))
                    return new Seen
                    {
                        Relation = standIn.Relation.ToString(),
                        State = "InInventory",
                        Red = InventoryIntegration.Project(standIn).ShowForeignStyle,
                        InInventory = PlayerInventory.Slot(netId) >= 0,
                        ClaimSlot = standIn.ClaimSlot
                    };
                if (!NetworkedItem.TryGet(netId, out NetworkedItem item) || item == null) return new Seen { State = "no item" };
                return new Seen
                {
                    Relation = item.Relation.ToString(),
                    State = item.GetItemState().ToString(),
                    Red = InventoryIntegration.Project(item).ShowForeignStyle,
                    InInventory = DV.InventorySystem.Inventory.Instance.IndexOf(item.gameObject) >= 0,
                    ClaimSlot = item.InventoryClaimSlot
                };
            }

            /// <summary>Host: the item's update as the server builds it for the client and for the
            /// host's own player (NetworkServer.BuildFor). The protocol tests prove what of it crosses the wire.
            /// An item in the client's inventory is data on the server (OI-COLD-06), so its object is made first.</summary>
            [Step]
            public static IEnumerator Told(Step<Told> step, ushort netId)
            {
                NetworkedItem item = null;
                bool made = false;
                PlayerItems.WithObject(netId, got => { item = got; made = true; });
                for (int i = 0; i < 300 && !made; i++) yield return null;
                if (!LiveItems.TryGet(netId, out var record) || item == null)
                    throw new Exception("no record for " + netId);
                NetworkServer server = NetworkServer.Instance;
                ItemUpdate update = item.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState);
                LiveItems.WriteToSnapshot(record, update, ItemTransitionReason.Unknown);
                ServerPlayer client = server.ServerPlayers.First(p => p != server.LocalPlayer && p.PlayerId != 0);
                ItemUpdate toClient = NetworkServer.BuildFor(update, client);
                ItemUpdate toHost = server.LocalPlayer == null ? null : NetworkServer.BuildFor(update, server.LocalPlayer);
                step.Result = new Told
                {
                    Client = toClient.Relation.ToString(),
                    ClientClaim = toClient.HasClaim,
                    Host = toHost?.Relation.ToString() ?? "",
                    HostClaim = toHost?.HasClaim == true,
                    HostCopy = server.LocalPlayer == null ? "" : item.Relation.ToString(),
                    Owner = record.Owner.ToString("D"),
                    OwnerConnected = PlayerNumbers.IsConnected(record.Owner)
                };
            }
        }
    }
}
