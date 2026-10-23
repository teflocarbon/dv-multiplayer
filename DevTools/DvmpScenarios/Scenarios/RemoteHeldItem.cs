using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Dvmp.Lib;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A client's held and carried item stays the client's on the server (playtest 2, dedicated
    /// host). The server's copy of an item a client holds mirrors the client's. When it changed
    /// (taken in a hand, a lantern lit), the server applied that as a change by its own player,
    /// booked the item to that player (a dedicated host's parked body), and the next update took it
    /// out of the client's hand: lanterns vanished as they were lit. The client picks up a lantern
    /// the host spawned beside it, lights it in its hand, and puts it in its inventory. After each
    /// step the client still has it, and the host records it in the client's hand or inventory, not
    /// its own player's. Also counts the server's backstop warnings ("kept it theirs"): each one is
    /// a path that still books a client's item to the server's player. Meant for a dedicated host,
    /// where playtest 2 found it; runs on a player host too.
    /// </summary>
    [Scenario("remote-held-item", "A client picks up a lantern, lights it and puts it away: after each step the host records it in the client's hand or inventory, never its own player's")]
    public sealed class RemoteHeldItem : Scenario<RemoteHeldItem.Options>
    {
        public sealed class Options
        {
            /// <summary>The item's prefab (Lantern, EOTLantern, Flashlight...).</summary>
            public string Prefab = "Lantern";
        }

        protected override async Task Run(Pair pair, Options o)
        {
            Report.Note("dedicated host: " + pair.Dedicated);
            long cursor = await pair.Host.LogCursor();
            ushort item = await pair.Host.Run<ushort>(s => Items.Spawn(s, o.Prefab, "client"));
            Report.Note($"client picks up {o.Prefab} {item}, lights it, puts it away");

            await pair.Client.Run(() => Items.Pickup(item), 60);
            await Check(pair, item, o.Prefab, "picked up", "InHand", "PlayerHand");
            Require.That(await pair.Client.Run<bool>(s => InGame.Light(s, item)), $"the {o.Prefab} lit on the client");
            await Check(pair, item, o.Prefab, "lit in the hand", "InHand", "PlayerHand");
            await pair.Client.Run(() => InGame.PutAway(item));
            await Check(pair, item, o.Prefab, "put in the inventory", "InInventory", "PlayerInventory");

            int backstop = (await pair.Host.LogsSince(cursor, "warning")).Count(l => l.Message != null && l.Message.Contains("kept it theirs"));
            Report.Note($"the server's backstop acted {backstop} time(s)");
        }

        private static async Task Check(Pair pair, ushort item, string prefab, string step, string clientState, string hostPlacement)
        {
            await Task.Delay(4000);   // the step's updates reach the host and come back
            Held c = await pair.Client.Run(() => InGame.ClientHas(item));
            Held h = await pair.Host.Run(() => InGame.HostHas(item));
            Report.Note($"{step}: client has it {c.State} (lit {c.Lit}); host records {h.State} of player {h.Player}, the client is {h.Client} (host copy lit {h.Lit})");
            Expect.Equal(clientState, c.State, $"after '{step}': where the client has its {prefab}");
            Expect.Equal(h.Client, h.Player, $"after '{step}': the player the host books the {prefab} to (the client's number)");
            Expect.Equal(hostPlacement, h.State, $"after '{step}': the host's record of the {prefab}");
        }

        public sealed class Held
        {
            /// <summary>Client: InHand, InInventory or the item's own state ("gone"); host: the record's placement ("none").</summary>
            public string State;
            public bool Lit;
            public int Player, Client;
        }

        // ---- runs inside the games
        public static class InGame
        {
            /// <summary>Client: where its item is (in its hand, its inventory, or the item's own
            /// state), and whether it's lit.</summary>
            [Step]
            public static Held ClientHas(ushort id)
            {
                if (!NetworkedItem.TryGet(id, out NetworkedItem n) || n == null) return new Held { State = "gone" };
                var lantern = n.GetComponentInChildren<Lantern>(true);
                string state = Hands.Held == n ? "InHand"
                    : DV.InventorySystem.Inventory.Instance.IndexOf(n.gameObject) >= 0 ? "InInventory"
                    : n.GetItemState().ToString();
                return new Held { State = state, Lit = lantern != null && lantern.Ignited };
            }

            /// <summary>Host: the server's record of the item (placement and player), the client's
            /// player number, and whether the server's copy is lit.</summary>
            [Step]
            public static Held HostHas(ushort id)
            {
                var server = Multiplayer.Server.Session.NetworkServer.Instance;
                var client = server.ServerPlayers.First(p => p.PlayerId != server.SelfId);
                AuthoritativeItemRegistry.TryGet(id, out var r);
                NetworkedItem.TryGet(id, out NetworkedItem n);
                var lantern = n == null ? null : n.GetComponentInChildren<Lantern>(true);
                return new Held { State = r?.Placement.ToString() ?? "none", Player = r?.PlacementPlayerId ?? 0, Client = client.PlayerId, Lit = lantern != null && lantern.Ignited };
            }

            /// <summary>Client: lights the lantern it holds, the way a lighter does: wick up, then
            /// ignite. An item with no flame isn't changed (true, so the run goes on).</summary>
            [Step]
            public static IEnumerator Light(Step<bool> step, ushort id)
            {
                NetworkedItem.TryGet(id, out NetworkedItem n);
                var lantern = n.GetComponentInChildren<Lantern>(true);
                if (lantern != null)
                {
                    lantern.UpdateWickRelatedLogic(1f);
                    yield return new WaitForSecondsRealtime(0.5f);
                    lantern.Ignite(1f);
                }
                yield return new WaitForSecondsRealtime(1f);
                step.Result = lantern == null || lantern.Ignited;
            }

            /// <summary>Client: puts the held item in the inventory and gives the game its input back.</summary>
            [Step]
            public static IEnumerator PutAway(ushort id)
            {
                NetworkedItem.TryGet(id, out NetworkedItem n);
                DV.InventorySystem.Inventory.Instance.AddItemToInventory(n.gameObject);
                yield return new WaitForSecondsRealtime(1f);
                Hands.ReleaseControl();
            }
        }
    }
}
